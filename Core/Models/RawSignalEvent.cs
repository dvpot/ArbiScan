using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record RawSignalEvent(
    DateTimeOffset TimestampUtc,
    string Symbol,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    decimal BinanceBestBid,
    decimal BinanceBestAsk,
    decimal BybitBestBid,
    decimal BybitBestAsk,
    decimal GrossSpreadUsd,
    decimal GrossSpreadBps,
    decimal BuyFeeUsd,
    decimal SellFeeUsd,
    decimal TotalFeesUsd,
    decimal SafetyBufferUsd,
    decimal NetEdgeUsd,
    decimal NetEdgeBps,
    decimal ExpectedNetPnlUsd,
    decimal ExpectedNetPnlPct,
    SignalClass SignalClass,
    DataHealthFlags HealthFlags);
