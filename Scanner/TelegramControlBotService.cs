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

namespace ArbiScan.Scanner;

public sealed class TelegramControlBotService : BackgroundService
{
    private readonly TelegramSettings _telegramSettings;
    private readonly ITelegramBotClient? _botClient;
    private readonly AppSettingsCatalog _catalog;
    private readonly RuntimeStateTracker _runtimeStateTracker;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly ILogger<TelegramControlBotService> _logger;

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

        var commandText = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            await ReplyAsync(botClient, message.Chat.Id, BuildHelpText(), cancellationToken);
            return;
        }

        try
        {
            var response = await ExecuteCommandAsync(botClient, message, commandText, cancellationToken);
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
        var parts = rawCommand.Split('\n', 2);
        var commandLine = parts[0].Trim();
        var payload = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        var tokens = commandLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = tokens[0].Split('@')[0].ToLowerInvariant();

        return command switch
        {
            "/start" or "/help" => BuildHelpText(),
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

    private async Task<string> BuildStatusTextAsync(CancellationToken cancellationToken)
    {
        var status = _runtimeStateTracker.GetSnapshot();
        var presetName = await _catalog.GetMatchingPresetNameAsync(cancellationToken) ?? "custom";
        var best = status.BestEvaluation;

        var bestLine = best is null
            ? "Лучший сетап: пока нет."
            : $"Лучший сетап: {FormatDirection(best.Direction)} | {best.TestNotionalUsd:0.########} USD | класс {FormatSignalClass(best.SignalClass)} | net {best.NetEdgeUsd:+0.########;-0.########;0} USD.";

        return $"Статус бота: {status.State}\n" +
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

    private static async Task ReplyAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken)
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: text,
            cancellationToken: cancellationToken);
    }

    private static string BuildHelpText() =>
        """
        Команды Telegram control:
        /status - текущий runtime-статус сканера
        /settings - показать текущий appsettings.json
        /presets - показать список preset из /srv/ArbiScan/config/appsettings
        /set <путь> <json-значение> - исправить текущее значение в appsettings.json
        /save_preset <имя> - сохранить текущий appsettings.json в список preset
        /use_preset <имя> - выбрать preset из списка и записать его в текущий appsettings.json
        /upsert_preset <имя> - добавить или обновить preset JSON-текстом или `.json` документом
        /restart - перезапустить бота с текущим appsettings.json
        """;

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

    private static string FormatExchange(ExchangeMarketSnapshot? snapshot) =>
        snapshot is null
            ? "нет данных"
            : $"{snapshot.BestBidPrice:0.########}/{snapshot.BestAskPrice:0.########}, age {snapshot.DataAge.TotalMilliseconds:0} ms, connected={snapshot.IsConnected}";
}
