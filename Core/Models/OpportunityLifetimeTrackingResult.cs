namespace ArbiScan.Core.Models;

public sealed record OpportunityLifetimeTrackingResult(
    IReadOnlyList<OpportunityWindowEvent> ClosedWindows,
    bool RejectedDueToMinLifetime);
