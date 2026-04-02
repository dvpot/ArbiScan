using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IMarketDataSource
{
    ExchangeMarketSnapshot? GetSnapshot();
}
