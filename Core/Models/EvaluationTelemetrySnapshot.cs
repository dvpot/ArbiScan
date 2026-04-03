using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record EvaluationTelemetrySnapshot(
    DateTimeOffset TimestampUtc,
    string Symbol,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    bool IsRawPositiveCross,
    bool IsProfitable,
    bool IsSnapshotUsableForEvaluation,
    FillabilityStatus FillabilityStatus,
    decimal GrossPnlUsd,
    decimal GrossEdgePct,
    decimal NetEdgeBeforeFeesPct,
    decimal FeesTotalUsd,
    decimal BuffersTotalUsd,
    decimal FillableBaseQuantity,
    DataHealthFlags HealthFlags,
    IReadOnlyList<string> RejectReasons,
    decimal BinanceBestBid,
    decimal BinanceBestAsk,
    decimal BybitBestBid,
    decimal BybitBestAsk);
