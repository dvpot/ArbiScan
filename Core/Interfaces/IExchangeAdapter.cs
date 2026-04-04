using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IExchangeAdapter
{
    ExchangeId Exchange { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    ExchangeMarketSnapshot? GetSnapshot();
}
