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
    private const string MenuMain = "menu:main";
    private const string MenuStatus = "menu:status";
    private const string MenuSettings = "menu:settings";
    private const string MenuPresets = "menu:presets";
    private const string MenuUsePreset = "menu:use_preset";
    private const string MenuSavePreset = "menu:save_preset";
    private const string MenuUploadPreset = "menu:upload_preset";
    private const string MenuEditSettings = "menu:edit_settings";
    private const string MenuEditBasic = "menu:edit_basic";
    private const string MenuEditThresholds = "menu:edit_thresholds";
    private const string MenuEditTiming = "menu:edit_timing";
    private const string MenuEditExport = "menu:edit_export";
    private const string MenuRestart = "menu:restart";
    private const string MenuHelp = "menu:help";

    private sealed class ConversationState
    {
        public PendingAction Action { get; set; } = PendingAction.None;
        public string? DraftPresetName { get; set; }
        public string? DraftSettingPath { get; set; }
    }

    private enum PendingAction
    {
        None = 0,
        AwaitingPresetSaveName = 1,
        AwaitingPresetUploadName = 2,
        AwaitingPresetUploadJson = 3,
        AwaitingCustomSettingValue = 4,
        AwaitingCustomSymbol = 5
    }

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
                    AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
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
        if (update.CallbackQuery is not null)
        {
            await HandleCallbackAsync(botClient, update.CallbackQuery, cancellationToken);
            return;
        }

        if (update.Message is not null)
        {
            await HandleMessageAsync(botClient, update.Message, cancellationToken);
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(message.From?.Id))
        {
            _logger.LogWarning(
                "Ignored unauthorized Telegram control message. UserId={UserId}, ChatId={ChatId}",
                message.From?.Id,
                message.Chat.Id);
            return;
        }

        var text = message.Text?.Trim() ?? message.Caption?.Trim() ?? string.Empty;
        var state = GetState(message.Chat.Id);

        try
        {
            if (state.Action != PendingAction.None || message.Document is not null)
            {
                var response = await HandleConversationMessageAsync(botClient, message, state, text, cancellationToken);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await ReplyAsync(botClient, message.Chat.Id, response, cancellationToken, CreateMainMenuMarkup());
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await SendMainMenuAsync(botClient, message.Chat.Id, cancellationToken);
                return;
            }

            if (text.StartsWith('/'))
            {
                var response = await HandleSlashCommandAsync(botClient, message, text, cancellationToken);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await ReplyAsync(botClient, message.Chat.Id, response, cancellationToken, CreateMainMenuMarkup());
                }

                return;
            }

            await SendMainMenuAsync(botClient, message.Chat.Id, cancellationToken);
        }
        catch (ValidationException ex)
        {
            await ReplyAsync(botClient, message.Chat.Id, $"Ошибка валидации: {ex.Message}", cancellationToken, CreateMainMenuMarkup());
        }
        catch (FileNotFoundException ex)
        {
            await ReplyAsync(botClient, message.Chat.Id, ex.Message, cancellationToken, CreateMainMenuMarkup());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram control message handling failed");
            await ReplyAsync(botClient, message.Chat.Id, $"Команда не выполнена: {ex.Message}", cancellationToken, CreateMainMenuMarkup());
        }
    }

    private async Task HandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(callbackQuery.From.Id))
        {
            _logger.LogWarning("Ignored unauthorized Telegram callback. UserId={UserId}", callbackQuery.From.Id);
            return;
        }

        if (callbackQuery.Message is null || string.IsNullOrWhiteSpace(callbackQuery.Data))
        {
            return;
        }

        try
        {
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            await HandleCallbackDataAsync(botClient, callbackQuery.Message.Chat.Id, callbackQuery.Data, cancellationToken);
        }
        catch (ValidationException ex)
        {
            await ReplyAsync(botClient, callbackQuery.Message.Chat.Id, $"Ошибка валидации: {ex.Message}", cancellationToken, CreateMainMenuMarkup());
        }
        catch (FileNotFoundException ex)
        {
            await ReplyAsync(botClient, callbackQuery.Message.Chat.Id, ex.Message, cancellationToken, CreateMainMenuMarkup());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram callback handling failed");
            await ReplyAsync(botClient, callbackQuery.Message.Chat.Id, $"Команда не выполнена: {ex.Message}", cancellationToken, CreateMainMenuMarkup());
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

    private async Task<string> HandleSlashCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        string commandText,
        CancellationToken cancellationToken)
    {
        var parts = commandText.Split('\n', 2);
        var commandLine = parts[0];
        var payload = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        var tokens = commandLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = tokens[0].Split('@')[0].ToLowerInvariant();

        return command switch
        {
            "/start" => await SendMainMenuAsync(botClient, message.Chat.Id, cancellationToken),
            "/help" => BuildHelpText(),
            "/status" => await BuildStatusTextAsync(cancellationToken),
            "/settings" => await BuildCurrentSettingsTextAsync(cancellationToken),
            "/presets" => await BuildPresetsTextAsync(cancellationToken),
            "/use_preset" => await HandleLegacyUsePresetAsync(tokens, cancellationToken),
            "/save_preset" => await HandleLegacySavePresetAsync(tokens, cancellationToken),
            "/upsert_preset" => await HandleLegacyUpsertPresetAsync(botClient, message, tokens, payload, cancellationToken),
            "/set" => await HandleLegacySetAsync(tokens, cancellationToken),
            "/restart" => await HandleRestartAsync(botClient, message.Chat.Id, cancellationToken),
            _ => "Неизвестная команда. Нажмите /start и работайте через кнопки."
        };
    }

    private async Task<string> HandleConversationMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        ConversationState state,
        string text,
        CancellationToken cancellationToken)
    {
        switch (state.Action)
        {
            case PendingAction.AwaitingPresetSaveName:
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ValidationException("Введите имя пресета.");
                }

                var presetName = await _catalog.SaveCurrentAsPresetAsync(text, cancellationToken);
                ResetState(message.Chat.Id);
                return $"Текущие настройки сохранены как пресет `{presetName}`.";
            }

            case PendingAction.AwaitingPresetUploadName:
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ValidationException("Введите имя пресета.");
                }

                SetState(message.Chat.Id, new ConversationState
                {
                    Action = PendingAction.AwaitingPresetUploadJson,
                    DraftPresetName = text
                });

                return "Имя принято. Теперь пришлите JSON текстом или `.json` файлом.";
            }

            case PendingAction.AwaitingPresetUploadJson:
            {
                var presetName = state.DraftPresetName
                    ?? throw new ValidationException("Не удалось определить имя пресета.");

                string json;
                if (message.Document is not null)
                {
                    json = await DownloadDocumentTextAsync(botClient, message.Document, cancellationToken);
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    json = text;
                }
                else
                {
                    throw new ValidationException("Нужен JSON текст или `.json` файл.");
                }

                presetName = await _catalog.UpsertPresetAsync(presetName, json, cancellationToken);
                ResetState(message.Chat.Id);
                return $"Пресет `{presetName}` сохранён.";
            }

            case PendingAction.AwaitingCustomSettingValue:
            {
                var path = state.DraftSettingPath
                    ?? throw new ValidationException("Не удалось определить параметр.");

                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ValidationException("Отправьте новое значение.");
                }

                await ApplySettingChangeAsync(path, text, cancellationToken);
                ResetState(message.Chat.Id);
                return $"Параметр `{path}` обновлён. Если нужно применить его сразу, нажмите кнопку «Перезапустить бота» в меню.";
            }

            case PendingAction.AwaitingCustomSymbol:
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ValidationException("Введите символ, например DOGEUSDT.");
                }

                await ApplySymbolAsync(text, cancellationToken);
                ResetState(message.Chat.Id);
                return $"Символ обновлён на `{text.ToUpperInvariant()}`. Если нужно применить сразу, нажмите «Перезапустить бота».";
            }

            default:
                return await SendMainMenuAsync(botClient, message.Chat.Id, cancellationToken);
        }
    }

    private async Task HandleCallbackDataAsync(
        ITelegramBotClient botClient,
        long chatId,
        string data,
        CancellationToken cancellationToken)
    {
        if (data == MenuMain)
        {
            await SendMainMenuAsync(botClient, chatId, cancellationToken);
            return;
        }

        if (data == MenuStatus)
        {
            await ReplyAsync(botClient, chatId, await BuildStatusTextAsync(cancellationToken), cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data == MenuSettings)
        {
            await ReplyAsync(botClient, chatId, await BuildCurrentSettingsTextAsync(cancellationToken), cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data == MenuPresets)
        {
            await ReplyAsync(botClient, chatId, await BuildPresetsTextAsync(cancellationToken), cancellationToken, CreatePresetsMenuMarkup());
            return;
        }

        if (data == MenuUsePreset)
        {
            await ReplyAsync(botClient, chatId, BuildPresetSelectionText(), cancellationToken, CreatePresetSelectionMarkup());
            return;
        }

        if (data == MenuSavePreset)
        {
            SetState(chatId, new ConversationState { Action = PendingAction.AwaitingPresetSaveName });
            await ReplyAsync(botClient, chatId, "Введите имя, под которым сохранить текущий appsettings.json в список пресетов.", cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data == MenuUploadPreset)
        {
            SetState(chatId, new ConversationState { Action = PendingAction.AwaitingPresetUploadName });
            await ReplyAsync(botClient, chatId, "Введите имя пресета, который нужно добавить или обновить.", cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data == MenuEditSettings)
        {
            await ReplyAsync(botClient, chatId, "Выберите группу параметров.", cancellationToken, CreateSettingsSectionsMarkup());
            return;
        }

        if (data == MenuEditBasic)
        {
            await ReplyAsync(botClient, chatId, await BuildBasicSettingsTextAsync(cancellationToken), cancellationToken, CreateBasicSettingsMarkup());
            return;
        }

        if (data == MenuEditThresholds)
        {
            await ReplyAsync(botClient, chatId, await BuildThresholdSettingsTextAsync(cancellationToken), cancellationToken, CreateThresholdSettingsMarkup());
            return;
        }

        if (data == MenuEditTiming)
        {
            await ReplyAsync(botClient, chatId, await BuildTimingSettingsTextAsync(cancellationToken), cancellationToken, CreateTimingSettingsMarkup());
            return;
        }

        if (data == MenuEditExport)
        {
            await ReplyAsync(botClient, chatId, await BuildExportSettingsTextAsync(cancellationToken), cancellationToken, CreateExportSettingsMarkup());
            return;
        }

        if (data == MenuRestart)
        {
            await HandleRestartAsync(botClient, chatId, cancellationToken);
            return;
        }

        if (data == MenuHelp)
        {
            await ReplyAsync(botClient, chatId, BuildHelpText(), cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data.StartsWith("preset:select:", StringComparison.Ordinal))
        {
            var presetName = data["preset:select:".Length..];
            presetName = await _catalog.ActivatePresetAsync(presetName, cancellationToken);
            await ReplyAsync(botClient, chatId, $"Пресет `{presetName}` выбран. Если нужно применить его сразу, нажмите «Перезапустить бота».", cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data.StartsWith("setting:quick:", StringComparison.Ordinal))
        {
            var payload = data["setting:quick:".Length..];
            var separatorIndex = payload.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                throw new ValidationException("Некорректное значение кнопки настройки.");
            }

            var path = payload[..separatorIndex];
            var value = payload[(separatorIndex + 1)..];
            await ApplyQuickSettingChangeAsync(path, value, cancellationToken);
            await ReplyAsync(botClient, chatId, $"Параметр `{path}` обновлён. Если нужно применить его сразу, нажмите «Перезапустить бота».", cancellationToken, CreateMainMenuMarkup());
            return;
        }

        if (data.StartsWith("setting:custom:", StringComparison.Ordinal))
        {
            var path = data["setting:custom:".Length..];
            if (string.Equals(path, "Symbol", StringComparison.Ordinal))
            {
                SetState(chatId, new ConversationState { Action = PendingAction.AwaitingCustomSymbol });
                await ReplyAsync(botClient, chatId, "Введите символ вручную, например DOGEUSDT.", cancellationToken, CreateMainMenuMarkup());
                return;
            }

            SetState(chatId, new ConversationState
            {
                Action = PendingAction.AwaitingCustomSettingValue,
                DraftSettingPath = path
            });

            await ReplyAsync(botClient, chatId, BuildCustomValuePrompt(path), cancellationToken, CreateMainMenuMarkup());
            return;
        }

        throw new ValidationException("Неизвестное действие кнопки.");
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
            return Task.FromResult("Список пресетов пуст. Каталог: /srv/ArbiScan/config/appsettings");
        }

        return Task.FromResult(
            "Доступные пресеты:\n" +
            string.Join('\n', names.Select(x => $"- {x}")));
    }

    private async Task<string> BuildBasicSettingsTextAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCurrentSettingsAsync(cancellationToken);
        return $"Основные настройки:\n" +
               $"Symbol = {settings.Symbol}\n" +
               $"BaseAsset = {settings.BaseAsset}\n" +
               $"QuoteAsset = {settings.QuoteAsset}\n" +
               $"TestNotionalsUsd = [{string.Join(", ", settings.TestNotionalsUsd.Select(x => x.ToString("0.########")))}]";
    }

    private async Task<string> BuildThresholdSettingsTextAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCurrentSettingsAsync(cancellationToken);
        return $"Пороговые настройки:\n" +
               $"SafetyBufferBps = {settings.SafetyBufferBps}\n" +
               $"EntryThresholdUsd = {settings.EntryThresholdUsd}\n" +
               $"EntryThresholdBps = {settings.EntryThresholdBps}";
    }

    private async Task<string> BuildTimingSettingsTextAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCurrentSettingsAsync(cancellationToken);
        return $"Тайминги:\n" +
               $"ScanIntervalMs = {settings.ScanIntervalMs}\n" +
               $"QuoteStalenessThresholdMs = {settings.QuoteStalenessThresholdMs}\n" +
               $"CumulativeSummaryIntervalSeconds = {settings.CumulativeSummaryIntervalSeconds}";
    }

    private async Task<string> BuildExportSettingsTextAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCurrentSettingsAsync(cancellationToken);
        return $"Экспорт raw signals:\n" +
               $"RawSignalJsonExportMode = {settings.RawSignalJsonExportMode}";
    }

    private async Task<string> HandleLegacyUsePresetAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Используйте /start -> «Выбрать пресет» или укажите имя: /use_preset <имя>.");
        }

        var presetName = await _catalog.ActivatePresetAsync(tokens[1], cancellationToken);
        return $"Пресет `{presetName}` выбран.";
    }

    private async Task<string> HandleLegacySavePresetAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Используйте /start -> «Сохранить текущие настройки» или укажите имя: /save_preset <имя>.");
        }

        var presetName = await _catalog.SaveCurrentAsPresetAsync(tokens[1], cancellationToken);
        return $"Текущие настройки сохранены как `{presetName}`.";
    }

    private async Task<string> HandleLegacyUpsertPresetAsync(
        ITelegramBotClient botClient,
        Message message,
        string[] tokens,
        string payload,
        CancellationToken cancellationToken)
    {
        if (tokens.Length < 2)
        {
            throw new ValidationException("Используйте /start -> «Добавить или обновить пресет» или укажите имя: /upsert_preset <имя>.");
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
            throw new ValidationException("Нужен JSON текст или `.json` файл.");
        }

        var presetName = await _catalog.UpsertPresetAsync(tokens[1], json, cancellationToken);
        return $"Пресет `{presetName}` сохранён.";
    }

    private async Task<string> HandleLegacySetAsync(string[] tokens, CancellationToken cancellationToken)
    {
        if (tokens.Length < 3)
        {
            throw new ValidationException("Используйте /start -> «Изменить параметр» или /set <путь> <json-значение>.");
        }

        await ApplySettingChangeAsync(tokens[1], tokens[2], cancellationToken);
        return $"Параметр `{tokens[1]}` обновлён.";
    }

    private async Task<string> HandleRestartAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
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

        await ReplyAsync(botClient, chatId, "Перезапуск запрошен. Контейнер завершит процесс и поднимется снова с текущим appsettings.json.", cancellationToken, CreateMainMenuMarkup());
        return string.Empty;
    }

    private async Task<string> SendMainMenuAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        ResetState(chatId);
        await ReplyAsync(botClient, chatId, BuildStartText(), cancellationToken, CreateMainMenuMarkup());
        return string.Empty;
    }

    private async Task<AppSettings> LoadCurrentSettingsAsync(CancellationToken cancellationToken) =>
        SettingsValidator.ParseAndValidateAppSettingsJson(await _catalog.GetCurrentSettingsAsync(cancellationToken));

    private async Task ApplyQuickSettingChangeAsync(string path, string valueToken, CancellationToken cancellationToken)
    {
        switch (path)
        {
            case "Symbol":
                await ApplySymbolAsync(valueToken, cancellationToken);
                return;
            case "TestNotionalsUsd":
                await ApplySettingChangeAsync(path, $"[{string.Join(",", valueToken.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}]", cancellationToken);
                return;
            default:
                await ApplySettingChangeAsync(path, valueToken, cancellationToken);
                return;
        }
    }

    private async Task ApplySettingChangeAsync(string path, string jsonValue, CancellationToken cancellationToken)
    {
        await _catalog.PatchCurrentSettingsAsync(path, jsonValue, cancellationToken);
    }

    private async Task ApplySymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (!normalizedSymbol.EndsWith("USDT", StringComparison.Ordinal))
        {
            throw new ValidationException("Сейчас поддерживаются символы формата <BASE>USDT, например XRPUSDT.");
        }

        var baseAsset = normalizedSymbol[..^4];
        await _catalog.PatchCurrentSettingsAsync("Symbol", $"\"{normalizedSymbol}\"", cancellationToken);
        await _catalog.PatchCurrentSettingsAsync("BaseAsset", $"\"{baseAsset}\"", cancellationToken);
        await _catalog.PatchCurrentSettingsAsync("QuoteAsset", "\"USDT\"", cancellationToken);
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

    private bool IsAuthorized(long? userId) =>
        _telegramSettings.Enabled &&
        userId == _telegramSettings.AllowedUserId;

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

    private static InlineKeyboardMarkup CreateMainMenuMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Статус", MenuStatus),
                InlineKeyboardButton.WithCallbackData("Текущие настройки", MenuSettings)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Список пресетов", MenuPresets),
                InlineKeyboardButton.WithCallbackData("Выбрать пресет", MenuUsePreset)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Сохранить текущие настройки", MenuSavePreset)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Добавить или обновить пресет", MenuUploadPreset)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Изменить параметры", MenuEditSettings)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Перезапустить бота", MenuRestart),
                InlineKeyboardButton.WithCallbackData("Помощь", MenuHelp)
            }
        });

    private static InlineKeyboardMarkup CreatePresetsMenuMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Выбрать пресет", MenuUsePreset),
                InlineKeyboardButton.WithCallbackData("Назад", MenuMain)
            }
        });

    private InlineKeyboardMarkup CreatePresetSelectionMarkup()
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var chunk in _catalog.ListPresetNames().Chunk(2))
        {
            rows.Add(chunk
                .Select(x => InlineKeyboardButton.WithCallbackData(x, $"preset:select:{x}"))
                .ToArray());
        }

        rows.Add(
        [
            InlineKeyboardButton.WithCallbackData("Назад", MenuMain)
        ]);

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup CreateSettingsSectionsMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Основные", MenuEditBasic),
                InlineKeyboardButton.WithCallbackData("Пороги", MenuEditThresholds)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Тайминги", MenuEditTiming),
                InlineKeyboardButton.WithCallbackData("Экспорт", MenuEditExport)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Назад", MenuMain)
            }
        });

    private static InlineKeyboardMarkup CreateBasicSettingsMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Символ XRPUSDT", "setting:quick:Symbol:XRPUSDT"),
                InlineKeyboardButton.WithCallbackData("Символ DOGEUSDT", "setting:quick:Symbol:DOGEUSDT")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Символ TRXUSDT", "setting:quick:Symbol:TRXUSDT"),
                InlineKeyboardButton.WithCallbackData("Символ вручную", "setting:custom:Symbol")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Notionals 10/20/50", "setting:quick:TestNotionalsUsd:10-20-50"),
                InlineKeyboardButton.WithCallbackData("Notionals 10/20/50/100", "setting:quick:TestNotionalsUsd:10-20-50-100")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Notionals 50/100", "setting:quick:TestNotionalsUsd:50-100"),
                InlineKeyboardButton.WithCallbackData("Notionals вручную", "setting:custom:TestNotionalsUsd")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Назад", MenuEditSettings)
            }
        });

    private static InlineKeyboardMarkup CreateThresholdSettingsMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Buffer 1", "setting:quick:SafetyBufferBps:1"),
                InlineKeyboardButton.WithCallbackData("Buffer 2", "setting:quick:SafetyBufferBps:2"),
                InlineKeyboardButton.WithCallbackData("Buffer 5", "setting:quick:SafetyBufferBps:5")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Entry USD 0", "setting:quick:EntryThresholdUsd:0"),
                InlineKeyboardButton.WithCallbackData("Entry USD 0.01", "setting:quick:EntryThresholdUsd:0.01"),
                InlineKeyboardButton.WithCallbackData("Entry USD 0.05", "setting:quick:EntryThresholdUsd:0.05")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Entry BPS 0", "setting:quick:EntryThresholdBps:0"),
                InlineKeyboardButton.WithCallbackData("Entry BPS 1", "setting:quick:EntryThresholdBps:1"),
                InlineKeyboardButton.WithCallbackData("Entry BPS 2", "setting:quick:EntryThresholdBps:2")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Buffer вручную", "setting:custom:SafetyBufferBps"),
                InlineKeyboardButton.WithCallbackData("Entry USD вручную", "setting:custom:EntryThresholdUsd")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Entry BPS вручную", "setting:custom:EntryThresholdBps"),
                InlineKeyboardButton.WithCallbackData("Назад", MenuEditSettings)
            }
        });

    private static InlineKeyboardMarkup CreateTimingSettingsMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Scan 100 ms", "setting:quick:ScanIntervalMs:100"),
                InlineKeyboardButton.WithCallbackData("Scan 250 ms", "setting:quick:ScanIntervalMs:250"),
                InlineKeyboardButton.WithCallbackData("Scan 500 ms", "setting:quick:ScanIntervalMs:500")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Stale 2000 ms", "setting:quick:QuoteStalenessThresholdMs:2000"),
                InlineKeyboardButton.WithCallbackData("Stale 3000 ms", "setting:quick:QuoteStalenessThresholdMs:3000"),
                InlineKeyboardButton.WithCallbackData("Stale 5000 ms", "setting:quick:QuoteStalenessThresholdMs:5000")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Summary 1800 s", "setting:quick:CumulativeSummaryIntervalSeconds:1800"),
                InlineKeyboardButton.WithCallbackData("Summary 3600 s", "setting:quick:CumulativeSummaryIntervalSeconds:3600"),
                InlineKeyboardButton.WithCallbackData("Summary 14400 s", "setting:quick:CumulativeSummaryIntervalSeconds:14400")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Scan вручную", "setting:custom:ScanIntervalMs"),
                InlineKeyboardButton.WithCallbackData("Stale вручную", "setting:custom:QuoteStalenessThresholdMs")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Summary вручную", "setting:custom:CumulativeSummaryIntervalSeconds"),
                InlineKeyboardButton.WithCallbackData("Назад", MenuEditSettings)
            }
        });

    private static InlineKeyboardMarkup CreateExportSettingsMarkup() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("All", "setting:quick:RawSignalJsonExportMode:\"All\""),
                InlineKeyboardButton.WithCallbackData("PositiveOnly", "setting:quick:RawSignalJsonExportMode:\"PositiveOnly\"")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("FeePositiveAndAbove", "setting:quick:RawSignalJsonExportMode:\"FeePositiveAndAbove\"")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("NetPositiveAndAbove", "setting:quick:RawSignalJsonExportMode:\"NetPositiveAndAbove\"")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("EntryQualifiedOnly", "setting:quick:RawSignalJsonExportMode:\"EntryQualifiedOnly\""),
                InlineKeyboardButton.WithCallbackData("None", "setting:quick:RawSignalJsonExportMode:\"None\"")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Назад", MenuEditSettings)
            }
        });

    private static string BuildStartText() =>
        """
        Панель управления ArbiScan.

        Здесь можно:
        - посмотреть статус сканера
        - посмотреть текущие настройки
        - выбрать пресет из списка
        - сохранить текущие настройки как пресет
        - загрузить новый пресет из JSON
        - менять основные параметры кнопками
        - перезапустить бота с новыми настройками
        """;

    private static string BuildHelpText() =>
        """
        Как работать:
        1. Нажмите «Выбрать пресет», если хотите переключиться на готовый набор настроек.
        2. Нажмите «Изменить параметры», если хотите поменять отдельные значения.
        3. После изменений нажмите «Перезапустить бота», чтобы сканер перечитал текущий appsettings.json.

        Для сложных значений бот сам попросит ввести только новое значение.
        Названия параметров вручную писать больше не нужно.

        Старые slash-команды оставлены как резервный режим, но основной интерфейс теперь через кнопки.
        """;

    private static string BuildPresetSelectionText() =>
        """
        Выберите пресет из списка кнопками ниже.
        После выбора он будет записан в текущий appsettings.json.
        Затем при необходимости нажмите «Перезапустить бота».
        """;

    private static string BuildCustomValuePrompt(string path) =>
        path switch
        {
            "TestNotionalsUsd" => "Отправьте массив в JSON-формате, например:\n[10,20,50]",
            "ScanIntervalMs" => "Отправьте новое число, например:\n250",
            "QuoteStalenessThresholdMs" => "Отправьте новое число, например:\n3000",
            "CumulativeSummaryIntervalSeconds" => "Отправьте новое число, например:\n3600",
            "SafetyBufferBps" => "Отправьте новое число, например:\n2",
            "EntryThresholdUsd" => "Отправьте новое число, например:\n0.01",
            "EntryThresholdBps" => "Отправьте новое число, например:\n1",
            _ => $"Отправьте новое значение для `{path}` в JSON-формате."
        };

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
