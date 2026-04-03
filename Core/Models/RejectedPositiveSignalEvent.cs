using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record RejectedPositiveSignalEvent(
    DateTimeOffset TimestampUtc,
    string Symbol,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    decimal BinanceBestBid,
    decimal BinanceBestAsk,
    decimal BybitBestBid,
    decimal BybitBestAsk,
    decimal GrossEdgePct,
    decimal NetEdgeBeforeFeesPct,
    decimal FeesTotalUsd,
    decimal BuffersTotalUsd,
    FillabilityStatus FillabilityStatus,
    decimal FillableSize,
    IReadOnlyList<string> RejectReasons,
    DataHealthFlags HealthFlags,
    bool IsSnapshotUsableForEvaluation);
