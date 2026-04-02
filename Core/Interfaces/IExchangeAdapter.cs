using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IExchangeAdapter : IMarketDataSource, ISymbolRulesProvider
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
