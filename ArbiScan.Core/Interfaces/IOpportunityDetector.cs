using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IOpportunityDetector
{
    OpportunityPairEvaluation Evaluate(
        MarketDataSnapshot snapshot,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        AppSettings settings);
}
