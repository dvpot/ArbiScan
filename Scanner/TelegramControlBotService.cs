using System.ComponentModel.DataAnnotations;
using System.Text;
using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ArbiScan.Scanner;

public sealed class TelegramControlBotService : BackgroundService
{
    private sealed class ConversationState
    {
        public PendingAction Action { get; set; } = PendingAction.None;
        public string? DraftPresetName { get; set; }
        public string? DraftSettingPath { get; set; }
    }

    private enum PendingAction
    {
        None = 0,
        AwaitingPresetSelection = 1,
        AwaitingPresetSaveName = 2,
        AwaitingPresetUpsertName = 3,
        AwaitingPresetUpsertPayload = 4,
        AwaitingSettingPath = 5,
        AwaitingSettingValue = 6
    }

    private const string MenuStatus = "Статус";
    private const string MenuCurrentSettings = "Текущие настройки";
    private const string MenuPresets = "Список пресетов";
    private const string MenuUsePreset = "Выбрать пресет";
    private const string MenuSavePreset = "Сохранить текущие настройки";
    private const string MenuUploadPreset = "Добавить или обновить пресет";
    private const string MenuEditSetting = "Изменить параметр";
    private const string MenuRestart = "Перезапустить бота";
    private const string MenuHelp = "Помощь";
    private const string MenuBack = "Главное меню";

    private readonly TelegramSettings _telegramSettings;
    private readonly ITelegramBotClient? _botClient;
    private readonly AppSettingsCatalog _catalog;
    private readonly RuntimeStateTracker _runtimeStateTracker;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<TelegramControlBotService> _logger;
    private readonly Dictionary<long, ConversationState> _statesByChat = new();
    private readonly Lock _statesLock = new();

    public TelegramControlBotService(
        TelegramSettings telegramSettings,
        AppSettingsCatalog catalog,
        RuntimeStateTracker runtimeStateTracker,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<TelegramControlBotService> logger)
    {
        _telegramSettings = telegramSettings;
        _catalog = catalog;
        _runtimeStateTracker = runtimeStateTracker;
        _hostApplicationLifetime = hostApplicationLifetime;
        _logger = logger;

        if (_telegramSettings.Enabled)
        {
            _botClient = new TelegramBotClient(_telegramSettings.BotToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_telegramSettings.Enabled || _botClient is null)
        {
            return;
        }

        try
        {
            await _botClient.DeleteWebhook(dropPendingUpdates: false, cancellationToken: stoppingToken);
            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandlePollingErrorAsync,
                new ReceiverOptions
                {
                    AllowedUpdates = [UpdateType.Message]
                },
                stoppingToken);

            _logger.LogInformation("Telegram control bot started");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram control bot failed");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
        {
            return;
        }

        if (!IsAuthorized(message))
        {
            _logger.LogWarning(
                "Ignored unauthorized Telegram control update. UserId={UserId}, ChatId={ChatId}",
                message.From?.Id,
                message.Chat.Id);
            return;
        }

        var state = GetState(message.Chat.Id);
        var commandText = message.Text ?? message.Caption ?? string.Empty;
        var isPresetUploadFlow = state.Action == PendingAction.AwaitingPresetUpsertPayload && message.Document is not null;
        if (string.IsNullOrWhiteSpace(commandText) && !isPresetUploadFlow)
        {
            await ReplyAsync(botClient, message.Chat.Id, BuildStartText(), cancellationToken, CreateMainMenuKeyboard());
            return;
        }

        try
        {
            var response = await ExecuteCommandAsync(botClient, message, isPresetUploadFlow ? "__document__" : commandText, cancellationToken);
            if (!string.IsNullOrWhiteSpace(response))
            {
                await ReplyAsync(botClient, message.Chat.Id, response, cancellationToken);
            }
        }
        catch (ValidationException ex)
        {
            await ReplyAsync(botClient, message.Chat.Id, $"Ошибка валидации: {ex.Message}", cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            await ReplyAsync(botClient, message.Chat.Id, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram control command failed");
            await ReplyAsync(botClient, message.Chat.Id, $"Команда не выполнена: {ex.Message}", cancellationToken);
        }
    }

    private Task HandlePollingErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException apiEx)
        {
            _logger.LogError(apiEx, "Telegram control polling API error from {Source}: {Message}", source, apiEx.Message);
        }
        else
        {
            _logger.LogError(exception, "Telegram control polling error from {Source}", source);
        }

        return Task.CompletedTask;
    }

    private async Task<string> ExecuteCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        string rawCommand,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var normalizedText = rawCommand.Trim();

        if (normalizedText.Equals(MenuBack, StringComparison.OrdinalIgnoreCase))
        {
            ResetState(chatId);
            await ReplyAsync(botClient, chatId, BuildStartText(), cancellationToken, CreateMainMenuKeyboard());
            return string.Empty;
        }

        if (TryHandleMenuAction(normalizedText, out var menuResponse))
        {
            ResetState(chatId);
            return await ExecuteMenuActionAsync(botClient, message, normalizedText, cancellationToken);
        }

        if (TryHandleConversationInput(normalizedText, message, out var conversationTask))
        {
            return await conversationTask(cancellationToken);
        }

        var parts = rawCommand.Split('\n', 2);
        var commandLine = parts[0].Trim();
        var payload = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        var tokens = commandLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = tokens[0].Split('@')[0].ToLowerInvariant();

        return command switch
        {
            "/start" => await HandleStartAsync(botClient, message.Chat.Id, cancellationToken),
            "/help" => await HandleHelpAsync(botClient, message.Chat.Id, cancellationToken),
            "/status" => await BuildStatusTextAsync(cancellationToken),
            "/settings" => await BuildCurrentSettingsTextAsync(cancellationToken),
            "/presets" => await BuildPresetsTextAsync(cancellationToken),
            "/set" => await HandleSetCommandAsync(tokens, cancellationToken),
            "/save_preset" => await HandleSavePresetCommandAsync(tokens, cancellationToken),
            "/use_preset" => await HandleUsePresetCommandAsync(tokens, cancellationToken),
            "/upsert_preset" => await HandleUpsertPresetCommandAsync(botClient, message, tokens, payload, cancellationToken),
            "/restart" => await HandleRestartCommandAsync(botClient, message.Chat.Id, cancellationToken),
            _ => $"Неизвестная команда.\n\n{BuildHelpText()}"
        };
    }

    private async Task<string> ExecuteMenuActionAsync(
        ITelegramBotClient botClient,
        Message message,
        string action,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        switch (action)
        {
            case MenuStatus:
                return await BuildStatusTextAsync(cancellationToken);
            case MenuCurrentSettings:
                return await BuildCurrentSettingsTextAsync(cancellationToken);
            case MenuPresets:
                return await BuildPresetsTextAsync(cancellationToken);
            case MenuUsePreset:
                await PromptPresetSelectionAsync(botClient, chatId, cancellationToken);
                return string.Empty;
            case MenuSavePreset:
                SetState(chatId, new ConversationState { Action = PendingAction.AwaitingPresetSaveName });
                return "Введите имя, под которым сохранить текущий appsettings.json в список пресетов.\nПример: dogeusdt.baseline";
            case MenuUploadPreset:
                SetState(chatId, new ConversationState { Action = PendingAction.AwaitingPresetUpsertName });
                return "Введите имя пресета, который нужно добавить или обновить.";
            case MenuEditSetting:
                SetState(chatId, new ConversationState { Action = PendingAction.AwaitingSettingPath });
                return "Введите путь к параметру.\nПримеры:\nSymbol\nTestNotionalsUsd\nStorage.RootPath\nEntryThresholdUsd";
            case MenuRestart:
                return await HandleRestartCommandAsync(botClient, chatId, cancellationToken);
            case MenuHelp:
                return BuildHelpText();
            default:
                return BuildHelpText();
        }
    }

    private async Task<string> BuildStatusTextAsync(CancellationToken cancellationToken)
    {
        var status = _runtimeStateTracker.GetSnapshot();
        var presetName = await _catalog.GetMatchingPresetNameAsync(cancellationToken) ?? "custom";
        var best = status.BestEvaluation;

        var bestLine = best is null
            ? "Лучший сетап: пока нет."
            : $"Лучший сетап: {FormatDirection(best.Direction)} | {best.TestNotionalUsd:0.########} USD | класс {FormatSignalClass(best.SignalClass)} | net {best.NetEdgeUsd:+0.########;-0.########;0} USD.";

        return $"Статус бота: {FormatRuntimeState(status.State)}\n" +
               $"Символ: {status.Symbol}\n" +
               $"Активный preset: {presetName}\n" +
               $"Запущен: {FormatDate(status.StartedAtUtc)}\n" +
               $"Последний цикл: {FormatDate(status.LastLoopAtUtc)}\n" +
               $"Health: {status.HealthFlags}\n" +
               $"{bestLine}\n" +
               $"Binance: {FormatExchange(status.Binance)}\n" +
               $"Bybit: {FormatExchange(status.Bybit)}\n" +
               $"Комментарий: {status.StatusMessage}";
    }

    private async Task<string> BuildCurrentSettingsTextAsync(CancellationToken cancellationToken)
    {
        var presetName = await _catalog.GetMatchingPresetNameAsync(cancellationToken) ?? "custom";
        var json = await _catalog.GetCurrentSettingsAsync(cancellationToken);
        return $"Текущий preset: {presetName}\n" +
               $"Файл: appsettings.json\n\n" +
               json;
    }

    private Task<string> BuildPresetsTextAsync(CancellationToken cancellationToken)
    {
        var names = _catalog.ListPresetNames();
        if (names.Count == 0)
        {
            return Task.FromResult("Список preset пуст. Каталог: /srv/ArbiScan/config/appsettings");
        }

        return Task.FromResult(
            "Доступные preset:\n" +
            string.Join('\n', names.Select(x => $"- {x}")));
    }

    private async Task<string> HandleSetCommandAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 3)
        {
            throw new ValidationException("Использование: /set <путь> <json-значение>. Пример: /set Symbol \"DOGEUSDT\"");
        }

        await _catalog.PatchCurrentSettingsAsync(tokens[1], tokens[2], cancellationToken);
        return $"Настройка `{tokens[1]}` обновлена. Чтобы применить изменения к работающему сканеру, выполните /restart.";
    }

    private async Task<string> HandleSavePresetCommandAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Использование: /save_preset <имя>");
        }

        var presetName = await _catalog.SaveCurrentAsPresetAsync(tokens[1], cancellationToken);
        return $"Текущий appsettings сохранён как preset `{presetName}`.";
    }

    private async Task<string> HandleUsePresetCommandAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Использование: /use_preset <имя>");
        }

        var presetName = await _catalog.ActivatePresetAsync(tokens[1], cancellationToken);
        return $"Preset `{presetName}` выбран и записан в текущий appsettings.json. Чтобы применить его в работающем сканере, выполните /restart.";
    }

    private async Task<string> HandleUpsertPresetCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        string[] tokens,
        string payload,
        CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Использование: /upsert_preset <имя> [json]. Можно прислать JSON текстом после перевода строки или приложить `.json` документ с этой подписью.");
        }

        string json;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            json = payload;
        }
        else if (message.Document is not null)
        {
            json = await DownloadDocumentTextAsync(botClient, message.Document, cancellationToken);
        }
        else
        {
            throw new ValidationException("Нужен JSON payload: либо в тексте команды после перевода строки, либо как приложенный `.json` документ.");
        }

        var presetName = await _catalog.UpsertPresetAsync(tokens[1], json, cancellationToken);
        return $"Preset `{presetName}` добавлен или обновлён в каталоге `/srv/ArbiScan/config/appsettings`.";
    }

    private async Task<string> HandleRestartCommandAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    _hostApplicationLifetime.StopApplication();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop application from Telegram restart command");
                }
            },
            CancellationToken.None);

        await ReplyAsync(botClient, chatId, "Перезапуск запрошен. Контейнер завершит процесс и поднимется снова с текущим appsettings.json.", cancellationToken);
        return string.Empty;
    }

    private async Task<string> HandleStartAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        ResetState(chatId);
        await ReplyAsync(botClient, chatId, BuildStartText(), cancellationToken, CreateMainMenuKeyboard());
        return string.Empty;
    }

    private async Task<string> HandleHelpAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        await ReplyAsync(botClient, chatId, BuildHelpText(), cancellationToken, CreateMainMenuKeyboard());
        return string.Empty;
    }

    private async Task PromptPresetSelectionAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var presetNames = _catalog.ListPresetNames();
        SetState(chatId, new ConversationState
        {
            Action = PendingAction.AwaitingPresetSelection
        });

        if (presetNames.Count == 0)
        {
            await ReplyAsync(botClient, chatId, "Список пресетов пуст. Сначала сохраните текущие настройки или загрузите новый пресет.", cancellationToken, CreateMainMenuKeyboard());
            ResetState(chatId);
            return;
        }

        await ReplyAsync(
            botClient,
            chatId,
            "Выберите пресет кнопкой ниже или отправьте его имя текстом.",
            cancellationToken,
            CreatePresetKeyboard(presetNames));
    }

    private async Task<string> DownloadDocumentTextAsync(ITelegramBotClient botClient, Document document, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        var file = await botClient.GetFile(document.FileId, cancellationToken);
        await botClient.DownloadFile(file, stream, cancellationToken);
        stream.Position = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private bool IsAuthorized(Message message) =>
        _telegramSettings.Enabled &&
        message.From?.Id == _telegramSettings.AllowedUserId;

    private bool TryHandleMenuAction(string text, out string menuAction)
    {
        var knownActions = new[]
        {
            MenuStatus,
            MenuCurrentSettings,
            MenuPresets,
            MenuUsePreset,
            MenuSavePreset,
            MenuUploadPreset,
            MenuEditSetting,
            MenuRestart,
            MenuHelp
        };

        menuAction = knownActions.FirstOrDefault(x => x.Equals(text, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(menuAction);
    }

    private bool TryHandleConversationInput(
        string normalizedText,
        Message message,
        out Func<CancellationToken, Task<string>> handler)
    {
        var state = GetState(message.Chat.Id);
        handler = _ => Task.FromResult(string.Empty);

        switch (state.Action)
        {
            case PendingAction.AwaitingPresetSelection:
                handler = async cancellationToken =>
                {
                    var presetName = await _catalog.ActivatePresetAsync(normalizedText, cancellationToken);
                    ResetState(message.Chat.Id);
                    return $"Пресет `{presetName}` выбран. Теперь можно нажать «Перезапустить бота», чтобы он стартовал уже с этими настройками.";
                };
                return true;

            case PendingAction.AwaitingPresetSaveName:
                handler = async cancellationToken =>
                {
                    var presetName = await _catalog.SaveCurrentAsPresetAsync(normalizedText, cancellationToken);
                    ResetState(message.Chat.Id);
                    return $"Текущие настройки сохранены как пресет `{presetName}`.";
                };
                return true;

            case PendingAction.AwaitingPresetUpsertName:
                handler = cancellationToken =>
                {
                    SetState(message.Chat.Id, new ConversationState
                    {
                        Action = PendingAction.AwaitingPresetUpsertPayload,
                        DraftPresetName = normalizedText
                    });

                    return Task.FromResult(
                        "Теперь пришлите JSON пресета.\nМожно просто вставить JSON текстом следующим сообщением или отправить `.json` файлом.");
                };
                return true;

            case PendingAction.AwaitingPresetUpsertPayload:
                handler = async cancellationToken =>
                {
                    var payload = message.Text ?? message.Caption;
                    string json;
                    if (message.Document is not null)
                    {
                        json = await DownloadDocumentTextAsync(_botClient!, message.Document, cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(payload))
                    {
                        json = payload;
                    }
                    else
                    {
                        throw new ValidationException("Нужен JSON текст или `.json` файл.");
                    }

                    var presetName = state.DraftPresetName
                        ?? throw new ValidationException("Не удалось определить имя пресета.");
                    presetName = await _catalog.UpsertPresetAsync(presetName, json, cancellationToken);
                    ResetState(message.Chat.Id);
                    return $"Пресет `{presetName}` сохранён в список.";
                };
                return true;

            case PendingAction.AwaitingSettingPath:
                handler = cancellationToken =>
                {
                    SetState(message.Chat.Id, new ConversationState
                    {
                        Action = PendingAction.AwaitingSettingValue,
                        DraftSettingPath = normalizedText
                    });

                    return Task.FromResult(
                        $"Путь `{normalizedText}` принят.\nТеперь отправьте новое значение в JSON-формате.\nПримеры:\n\"DOGEUSDT\"\n25\n[10,20,50]\n0.5");
                };
                return true;

            case PendingAction.AwaitingSettingValue:
                handler = async cancellationToken =>
                {
                    var path = state.DraftSettingPath
                        ?? throw new ValidationException("Не удалось определить путь к настройке.");
                    await _catalog.PatchCurrentSettingsAsync(path, normalizedText, cancellationToken);
                    ResetState(message.Chat.Id);
                    return $"Параметр `{path}` обновлён. Если настройка должна сразу примениться к сканеру, нажмите «Перезапустить бота».";
                };
                return true;

            default:
                return false;
        }
    }

    private ConversationState GetState(long chatId)
    {
        lock (_statesLock)
        {
            return _statesByChat.TryGetValue(chatId, out var state)
                ? state
                : new ConversationState();
        }
    }

    private void SetState(long chatId, ConversationState state)
    {
        lock (_statesLock)
        {
            _statesByChat[chatId] = state;
        }
    }

    private void ResetState(long chatId)
    {
        lock (_statesLock)
        {
            _statesByChat.Remove(chatId);
        }
    }

    private static async Task ReplyAsync(
        ITelegramBotClient botClient,
        long chatId,
        string text,
        CancellationToken cancellationToken,
        ReplyMarkup? replyMarkup = null)
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: text,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);
    }

    private static string BuildStartText() =>
        """
        Панель управления ArbiScan.

        Что можно делать из бота:
        - посмотреть текущий статус сканера
        - посмотреть активные настройки
        - выбрать пресет настроек из списка
        - сохранить текущие настройки как новый пресет
        - добавить или обновить пресет через JSON
        - точечно изменить отдельный параметр
        - перезапустить бота с новыми настройками

        Нажмите кнопку ниже.
        """;

    private static string BuildHelpText() =>
        """
        Работа через меню:
        - «Статус» показывает, жив ли сканер и какой сейчас лучший сетап
        - «Текущие настройки» показывает активный appsettings.json
        - «Список пресетов» показывает, что лежит в /srv/ArbiScan/config/appsettings
        - «Выбрать пресет» переключает текущий appsettings.json на один из пресетов
        - «Сохранить текущие настройки» кладёт текущий appsettings.json в список пресетов
        - «Добавить или обновить пресет» принимает JSON текстом или `.json` файлом
        - «Изменить параметр» пошагово меняет один конкретный параметр
        - «Перезапустить бота» перезапускает процесс и применяет текущий appsettings.json

        Для опытного режима старые команды тоже работают:
        /status
        /settings
        /presets
        /set <путь> <json-значение>
        /save_preset <имя>
        /use_preset <имя>
        /upsert_preset <имя>
        /restart
        """;

    private static ReplyKeyboardMarkup CreateMainMenuKeyboard() =>
        new(new[]
        {
            new KeyboardButton[] { MenuStatus, MenuCurrentSettings },
            new KeyboardButton[] { MenuPresets, MenuUsePreset },
            new KeyboardButton[] { MenuSavePreset, MenuUploadPreset },
            new KeyboardButton[] { MenuEditSetting, MenuRestart },
            new KeyboardButton[] { MenuHelp }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };

    private static ReplyKeyboardMarkup CreatePresetKeyboard(IReadOnlyList<string> presetNames)
    {
        var rows = new List<KeyboardButton[]>();
        foreach (var chunk in presetNames.Chunk(2))
        {
            rows.Add(chunk.Select(x => new KeyboardButton(x)).ToArray());
        }

        rows.Add([new KeyboardButton(MenuBack)]);

        return new ReplyKeyboardMarkup(rows)
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
    }

    private static string FormatDirection(ArbitrageDirection direction) =>
        direction switch
        {
            ArbitrageDirection.BuyBinanceSellBybit => "купить Binance / продать Bybit",
            ArbitrageDirection.BuyBybitSellBinance => "купить Bybit / продать Binance",
            _ => direction.ToString()
        };

    private static string FormatSignalClass(SignalClass signalClass) =>
        signalClass switch
        {
            SignalClass.EntryQualified => "entry-qualified",
            SignalClass.NetPositive => "net-positive",
            SignalClass.FeePositive => "fee-positive",
            SignalClass.RawPositive => "raw-positive",
            _ => "non-positive"
        };

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "n/a";

    private static string FormatRuntimeState(string state) =>
        state switch
        {
            "running" => "работает",
            "starting" => "запускается",
            "stopping" => "останавливается",
            "stopped" => "остановлен",
            "error" => "ошибка",
            _ => state
        };

    private static string FormatExchange(ExchangeMarketSnapshot? snapshot) =>
        snapshot is null
            ? "нет данных"
            : $"{snapshot.BestBidPrice:0.########}/{snapshot.BestAskPrice:0.########}, age {snapshot.DataAge.TotalMilliseconds:0} ms, connected={snapshot.IsConnected}";
}
