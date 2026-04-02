using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OpportunityWindowEvent(
    string WindowId,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset ClosedAtUtc,
    long LifetimeMs,
    string Symbol,
    ArbitrageDirection Direction,
    string BuyExchange,
    string SellExchange,
    decimal TestNotionalUsd,
    FillabilityStatus FillabilityStatus,
    decimal ExecutableQuantity,
    decimal FillableBaseQuantity,
    decimal BinanceBestBid,
    decimal BinanceBestAsk,
    decimal BybitBestBid,
    decimal BybitBestAsk,
    decimal GrossPnlUsd,
    decimal OptimisticNetPnlUsd,
    decimal ConservativeNetPnlUsd,
    decimal GrossEdgePct,
    decimal OptimisticNetEdgePct,
    decimal ConservativeNetEdgePct,
    decimal FeesTotalUsd,
    decimal BuffersTotalUsd,
    DataHealthFlags HealthFlags,
    string? Notes);
