using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed class ActiveOpportunityWindow
{
    public required string WindowId { get; init; }
    public required ArbitrageDirection Direction { get; init; }
    public required decimal TestNotionalUsd { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required OpportunityPairEvaluation OpeningEvaluation { get; set; }
    public required OpportunityPairEvaluation LatestEvaluation { get; set; }
    public required OpportunityPairEvaluation PeakEvaluation { get; set; }

    public long LifetimeMs(DateTimeOffset currentTimestampUtc) =>
        (long)(currentTimestampUtc - StartedAtUtc).TotalMilliseconds;
}
