namespace ArbiScan.Core.Models;

public sealed record SummaryDebugStats(
    int RawPositiveCrossCount,
    int RejectedDueToFeesCount,
    int RejectedDueToBuffersCount,
    int RejectedDueToHealthCount,
    int RejectedDueToMinLifetimeCount,
    int RejectedDueToFillabilityCount,
    int RejectedDueToRulesCount,
    int RejectedDueToOtherCount,
    int RejectedOnlyDueToFeesCount,
    int RejectedOnlyDueToFillabilityCount,
    int RejectedDueToFeesAndFillabilityCount,
    int RejectedDueToMultipleReasonsCount,
    int WouldBeProfitableWithoutFeesCount,
    int WouldBeProfitableWithoutFillabilityCount,
    IReadOnlyDictionary<string, int> RawPositiveCrossCountByDirection,
    IReadOnlyDictionary<string, int> PrimaryRejectReasonCounts,
    IReadOnlyDictionary<string, int> SecondaryRejectReasonCounts,
    IReadOnlyDictionary<string, int> RejectReasonCountsByDirection,
    IReadOnlyDictionary<string, int> RejectReasonCountsByNotional,
    IReadOnlyDictionary<string, int> RejectReasonCountsByDirectionAndNotional);
