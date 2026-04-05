using ArbiScan.Core.Configuration;
using ArbiScan.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace ArbiScan.Scanner;

public sealed class TelegramBotNotifier : BackgroundService, ITelegramNotifier
{
    private readonly TelegramSettings _settings;
    private readonly TelegramBotClient _botClient;
    private readonly ILogger<TelegramBotNotifier> _logger;
    private readonly Channel<string> _messageQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

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

        await _messageQueue.Writer.WriteAsync(message, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _messageQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_messageQueue.Reader.TryRead(out var message))
                {
                    await SendDirectAsync(message, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await DrainPendingMessagesAsync();
        }
    }

    private async Task DrainPendingMessagesAsync()
    {
        while (_messageQueue.Reader.TryRead(out var pendingMessage))
        {
            await SendDirectAsync(pendingMessage, CancellationToken.None);
        }
    }

    private async Task SendDirectAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _botClient.SendMessage(
                chatId: _settings.AllowedUserId,
                text: message,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogError(
                ex,
                "Telegram API error while sending notification. Type={ExceptionType}, Message={Message}, Inner={InnerMessage}, StatusCode={StatusCode}, Endpoint={Endpoint}, Method={Method}, RetryAttempt={RetryAttempt}",
                ex.GetType().FullName,
                ex.Message,
                ex.InnerException?.Message,
                ex.ErrorCode,
                "sendMessage",
                "POST",
                0);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected Telegram notification error. Type={ExceptionType}, Message={Message}, Inner={InnerMessage}, StatusCode={StatusCode}, Endpoint={Endpoint}, Method={Method}, RetryAttempt={RetryAttempt}",
                ex.GetType().FullName,
                ex.Message,
                ex.InnerException?.Message,
                null,
                "sendMessage",
                "POST",
                0);
        }
    }
}
