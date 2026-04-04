using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class SignalCalculator
{
    private readonly IFeeCalculator _feeCalculator;

    public SignalCalculator(IFeeCalculator feeCalculator)
    {
        _feeCalculator = feeCalculator;
    }

    public OpportunityEvaluation Evaluate(
        MarketDataSnapshot snapshot,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        AppSettings settings)
    {
        var buyExchange = direction.BuyExchange();
        var sellExchange = direction.SellExchange();
        var buy = snapshot.GetExchange(buyExchange);
        var sell = snapshot.GetExchange(sellExchange);

        if (buy is null || sell is null || !buy.HasQuote || !sell.HasQuote)
        {
            return new OpportunityEvaluation(
                snapshot.CapturedAtUtc,
                snapshot.Symbol,
                direction,
                buyExchange,
                sellExchange,
                testNotionalUsd,
                buy?.BestAskPrice ?? 0m,
                sell?.BestBidPrice ?? 0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                SignalClass.NonPositive,
                snapshot.HealthFlags,
                false);
        }

        var buyPrice = buy.BestAskPrice!.Value;
        var sellPrice = sell.BestBidPrice!.Value;
        var baseQuantity = buyPrice > 0m ? testNotionalUsd / buyPrice : 0m;
        var buyCostUsd = testNotionalUsd;
        var sellProceedsUsd = baseQuantity * sellPrice;
        var grossSpreadUsd = sellProceedsUsd - buyCostUsd;
        var grossSpreadBps = buyCostUsd > 0m ? grossSpreadUsd / buyCostUsd * 10_000m : 0m;
        var buyFeeUsd = _feeCalculator.CalculateFeeUsd(buyExchange, buyCostUsd);
        var sellFeeUsd = _feeCalculator.CalculateFeeUsd(sellExchange, sellProceedsUsd);
        var totalFeesUsd = buyFeeUsd + sellFeeUsd;
        var safetyBufferUsd = (buyCostUsd + sellProceedsUsd) * settings.SafetyBufferBps / 10_000m;
        var netEdgeUsd = sellProceedsUsd - buyCostUsd - totalFeesUsd - safetyBufferUsd;
        var netEdgeBps = buyCostUsd > 0m ? netEdgeUsd / buyCostUsd * 10_000m : 0m;
        var expectedNetPnlPct = buyCostUsd > 0m ? netEdgeUsd / buyCostUsd * 100m : 0m;

        return new OpportunityEvaluation(
            snapshot.CapturedAtUtc,
            snapshot.Symbol,
            direction,
            buyExchange,
            sellExchange,
            testNotionalUsd,
            buyPrice,
            sellPrice,
            baseQuantity,
            buyCostUsd,
            sellProceedsUsd,
            grossSpreadUsd,
            grossSpreadBps,
            buyFeeUsd,
            sellFeeUsd,
            totalFeesUsd,
            safetyBufferUsd,
            netEdgeUsd,
            netEdgeBps,
            netEdgeUsd,
            expectedNetPnlPct,
            Classify(grossSpreadUsd, totalFeesUsd, netEdgeUsd, netEdgeBps, settings),
            snapshot.HealthFlags,
            true);
    }

    private static SignalClass Classify(
        decimal grossSpreadUsd,
        decimal totalFeesUsd,
        decimal netEdgeUsd,
        decimal netEdgeBps,
        AppSettings settings)
    {
        if (grossSpreadUsd <= 0m)
        {
            return SignalClass.NonPositive;
        }

        if (grossSpreadUsd - totalFeesUsd <= 0m)
        {
            return SignalClass.RawPositive;
        }

        if (netEdgeUsd <= 0m)
        {
            return SignalClass.FeePositive;
        }

        if (netEdgeUsd < settings.EntryThresholdUsd || netEdgeBps < settings.EntryThresholdBps)
        {
            return SignalClass.NetPositive;
        }

        return SignalClass.EntryQualified;
    }
}
