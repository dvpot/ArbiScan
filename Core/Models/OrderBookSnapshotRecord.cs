using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OrderBookSnapshotRecord(
    DateTimeOffset TimestampUtc,
    ExchangeId Exchange,
    string Symbol,
    OrderBookSyncStatus Status,
    TimeSpan DataAge,
    decimal? BestBidPrice,
    decimal? BestBidQuantity,
    decimal? BestAskPrice,
    decimal? BestAskQuantity,
    string PayloadJson);
