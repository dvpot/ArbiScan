using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record FillabilityDiagnosticsReport(
    SummaryPeriod Period,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Symbol,
    int RawPositiveCandidateCount,
    IReadOnlyDictionary<string, int> FillableCountByNotional,
    IReadOnlyDictionary<string, int> PartiallyFillableCountByNotional,
    IReadOnlyDictionary<string, int> NotFillableCountByNotional,
    IReadOnlyDictionary<string, decimal> AverageRequiredQuantityByNotional,
    IReadOnlyDictionary<string, decimal> AverageAvailableTop1QuantityByNotional,
    IReadOnlyDictionary<string, decimal> AverageAvailableTopNQuantityByNotional,
    IReadOnlyDictionary<string, decimal> MedianAvailableTopNQuantityByNotional,
    IReadOnlyDictionary<string, decimal> AverageRoundedExecutableQuantityByNotional,
    IReadOnlyDictionary<string, int> TopReasonsOfNonFillability,
    IReadOnlyDictionary<string, int> TopReasonsOfNonFillabilityByNotional);
