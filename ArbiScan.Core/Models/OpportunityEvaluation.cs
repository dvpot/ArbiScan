using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OpportunityEvaluation(
    AnalysisMode Mode,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    FillabilityStatus FillabilityStatus,
    decimal ExecutableQuantity,
    decimal GrossPnlUsd,
    decimal NetPnlUsd,
    decimal GrossEdgePct,
    decimal NetEdgePct,
    decimal ExpectedRoiPct,
    decimal FeesTotalUsd,
    decimal BuffersTotalUsd,
    decimal FillableBaseQuantity,
    DataHealthFlags HealthFlags,
    string? RejectReason,
    TradeLegEstimate? BuyLeg,
    TradeLegEstimate? SellLeg)
{
    public bool IsProfitable(decimal minUsdThreshold, decimal minBpsThreshold) =>
        FillabilityStatus == FillabilityStatus.Fillable &&
        NetPnlUsd > 0m &&
        NetPnlUsd >= minUsdThreshold &&
        NetEdgePct * 100m >= minBpsThreshold;
}
