namespace ArbiScan.Core.Models;

public sealed record HealthReport(
    SummaryPeriod Period,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Symbol,
    long UptimeMs,
    int ReconnectCount,
    int StaleCount,
    long MaxStaleDurationMs,
    long TotalDegradedDurationMs,
    int MarketDataErrorCount,
    IReadOnlyDictionary<string, long> UnhealthyDurationMsByExchange,
    string FinalAssessment);
