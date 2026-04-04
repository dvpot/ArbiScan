using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OpportunityPairEvaluation(
    DateTimeOffset TimestampUtc,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    OpportunityEvaluation Optimistic,
    OpportunityEvaluation Conservative,
    decimal BinanceBestBid,
    decimal BinanceBestAsk,
    decimal BybitBestBid,
    decimal BybitBestAsk,
    DataHealthFlags HealthFlags,
    FillabilityDecisionDetails FillabilityDecision);
