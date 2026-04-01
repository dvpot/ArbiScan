namespace ArbiScan.Core.Interfaces;

public interface ITelegramNotifier
{
    bool IsEnabled { get; }
    Task SendMessageAsync(string message, CancellationToken cancellationToken);
}
