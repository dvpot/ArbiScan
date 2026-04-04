namespace ArbiScan.Core.Models;

public sealed record SummaryDebugStats(
    int RejectedByFeesCount,
    int RejectedBySafetyBufferCount,
    int RejectedByThresholdCount,
    IReadOnlyDictionary<string, int> SignalClassCountsByDirection,
    IReadOnlyDictionary<string, int> SignalClassCountsByNotional,
    IReadOnlyDictionary<string, int> NetEdgeDistribution);
