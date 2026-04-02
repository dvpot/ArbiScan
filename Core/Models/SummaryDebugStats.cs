namespace ArbiScan.Core.Models;

public sealed record SummaryDebugStats(
    int RawPositiveCrossCount,
    int RejectedDueToFeesCount,
    int RejectedDueToBuffersCount,
    int RejectedDueToHealthCount,
    int RejectedDueToMinLifetimeCount,
    int RejectedDueToFillabilityCount,
    int RejectedDueToRulesCount,
    int RejectedDueToOtherCount);
