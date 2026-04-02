namespace ArbiScan.Core.Models;

public sealed record HealthReport(
    SummaryPeriod Period,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Symbol,
    long HealthyDurationMs,
    long DegradedDurationMs,
    IReadOnlyDictionary<string, int> ReconnectCountByExchange,
    IReadOnlyDictionary<string, int> ResyncCountByExchange,
    IReadOnlyDictionary<string, int> StaleCountByExchange,
    IReadOnlyDictionary<string, int> TopDegradationCauses,
    IReadOnlyDictionary<string, long> LongestStaleIntervalMsByExchange,
    IReadOnlyDictionary<string, DateTimeOffset?> LastStaleDetectedAtUtcByExchange,
    IReadOnlyDictionary<string, DateTimeOffset?> LastReconnectAtUtcByExchange,
    IReadOnlyDictionary<string, DateTimeOffset?> LastResyncAtUtcByExchange);
