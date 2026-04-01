using ArbiScan.Core.Interfaces;

namespace ArbiScan.Infrastructure.Reporting;

public sealed class NullTelegramNotifier : ITelegramNotifier
{
    public bool IsEnabled => false;

    public Task SendMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
}
