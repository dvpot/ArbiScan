using ArbiScan.Core.Configuration;
using ArbiScan.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace ArbiScan.Scanner;

public sealed class TelegramBotNotifier : ITelegramNotifier
{
    private readonly TelegramSettings _settings;
    private readonly TelegramBotClient _botClient;
    private readonly ILogger<TelegramBotNotifier> _logger;

    public TelegramBotNotifier(TelegramSettings settings, ILogger<TelegramBotNotifier> logger)
    {
        _settings = settings;
        _logger = logger;
        _botClient = new TelegramBotClient(settings.BotToken);
    }

    public bool IsEnabled => _settings.Enabled;

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            await _botClient.SendMessage(
                chatId: _settings.AllowedUserId,
                text: message,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogError(ex, "Telegram API error while sending notification");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Telegram notification error");
        }
    }
}
