using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record ExchangeMarketSnapshot(
    ExchangeId Exchange,
    ExchangeSymbolRules Rules,
    OrderBookSnapshot OrderBook)
{
    public bool IsHealthy(TimeSpan maxAge) =>
        OrderBook.Status == OrderBookSyncStatus.Synced &&
        OrderBook.DataAge <= maxAge &&
        OrderBook.BestBid is not null &&
        OrderBook.BestAsk is not null;
}
