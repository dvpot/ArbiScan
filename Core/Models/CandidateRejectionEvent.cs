using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record CandidateRejectionEvent(
    DateTimeOffset TimestampUtc,
    string Symbol,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    string BuyExchange,
    string SellExchange,
    decimal BestAsk,
    decimal BestBid,
    decimal BuyTopOfBookQuantity,
    decimal SellTopOfBookQuantity,
    decimal BuyAggregatedFillableQuantity,
    decimal SellAggregatedFillableQuantity,
    decimal QuantityBeforeRounding,
    decimal QuantityAfterRoundingBinanceRules,
    decimal QuantityAfterRoundingBybitRules,
    decimal EffectiveExecutableQuantity,
    decimal GrossEdgeUsd,
    decimal GrossEdgeBps,
    decimal FeesUsd,
    decimal BuffersUsd,
    decimal NetEdgeUsd,
    decimal NetEdgeBps,
    IReadOnlyList<string> RejectReasons,
    string? PrimaryRejectReason,
    IReadOnlyList<string> SecondaryRejectReasons,
    FillabilityDecisionDetails FillabilityDecision);
