using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OrderBookSnapshot(
    string Symbol,
    OrderBookSyncStatus Status,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? UpdateTimeUtc,
    DateTimeOffset? UpdateServerTimeUtc,
    DateTimeOffset? LastUpdateCallbackUtc,
    TimeSpan DataAge,
    TimeSpan DataAgeByExchangeTimestamp,
    TimeSpan DataAgeByLocalCallbackTimestamp,
    DateTimeOffset? LastTopOfBookChangeUtc,
    TimeSpan TimeSinceTopOfBookChanged,
    IReadOnlyList<OrderBookLevel> Bids,
    IReadOnlyList<OrderBookLevel> Asks)
{
    public OrderBookLevel? BestBid => Bids.Count > 0 ? Bids[0] : null;
    public OrderBookLevel? BestAsk => Asks.Count > 0 ? Asks[0] : null;
}
