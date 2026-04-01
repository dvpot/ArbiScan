using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class OpportunityDetector : IOpportunityDetector
{
    private readonly IFeeCalculator _feeCalculator;
    private readonly IFillableSizeCalculator _fillableSizeCalculator;

    public OpportunityDetector(IFeeCalculator feeCalculator, IFillableSizeCalculator fillableSizeCalculator)
    {
        _feeCalculator = feeCalculator;
        _fillableSizeCalculator = fillableSizeCalculator;
    }

    public OpportunityPairEvaluation Evaluate(
        MarketDataSnapshot snapshot,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        AppSettings settings)
    {
        var buyExchange = direction.BuyExchange();
        var sellExchange = direction.SellExchange();
        var buyMarket = snapshot.GetExchange(buyExchange);
        var sellMarket = snapshot.GetExchange(sellExchange);

        if (buyMarket is null || sellMarket is null)
        {
            var empty = CreateRejectedEvaluation(AnalysisMode.Conservative, direction, testNotionalUsd, snapshot.HealthFlags | DataHealthFlags.MissingOrderBook, "Missing market snapshot");
            return new OpportunityPairEvaluation(snapshot.CapturedAtUtc, direction, testNotionalUsd, empty with { Mode = AnalysisMode.Optimistic }, empty, 0m, 0m, 0m, 0m, empty.HealthFlags);
        }

        var healthFlags = snapshot.HealthFlags;
        var buyLiquidityFromQuote = _fillableSizeCalculator.SweepQuote(buyMarket.OrderBook, testNotionalUsd, TradeSide.Buy);
        var sellLiquidityBase = _fillableSizeCalculator.GetAvailableBaseLiquidity(sellMarket.OrderBook, TradeSide.Sell);

        var executableBase = Math.Min(buyLiquidityFromQuote.ExecutedBaseQuantity, sellLiquidityBase);
        executableBase = SymbolRulesNormalizer.GetExecutableQuantity(executableBase, buyMarket.Rules, sellMarket.Rules);

        var optimistic = BuildEvaluation(
            AnalysisMode.Optimistic,
            direction,
            testNotionalUsd,
            healthFlags,
            buyMarket,
            sellMarket,
            executableBase,
            settings,
            0m);

        var conservativeBufferRate = settings.Buffers.LatencyBufferBps +
                                     settings.Buffers.SlippageBufferBps +
                                     settings.Buffers.AdditionalSafetyBufferBps;

        var conservative = BuildEvaluation(
            AnalysisMode.Conservative,
            direction,
            testNotionalUsd,
            healthFlags,
            buyMarket,
            sellMarket,
            executableBase,
            settings,
            conservativeBufferRate);

        return new OpportunityPairEvaluation(
            snapshot.CapturedAtUtc,
            direction,
            testNotionalUsd,
            optimistic,
            conservative,
            buyExchange == ExchangeId.Binance ? buyMarket.OrderBook.BestBid?.Price ?? 0m : sellMarket.OrderBook.BestBid?.Price ?? 0m,
            buyExchange == ExchangeId.Binance ? buyMarket.OrderBook.BestAsk?.Price ?? 0m : sellMarket.OrderBook.BestAsk?.Price ?? 0m,
            buyExchange == ExchangeId.Bybit ? buyMarket.OrderBook.BestBid?.Price ?? 0m : sellMarket.OrderBook.BestBid?.Price ?? 0m,
            buyExchange == ExchangeId.Bybit ? buyMarket.OrderBook.BestAsk?.Price ?? 0m : sellMarket.OrderBook.BestAsk?.Price ?? 0m,
            healthFlags);
    }

    private OpportunityEvaluation BuildEvaluation(
        AnalysisMode mode,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        DataHealthFlags healthFlags,
        ExchangeMarketSnapshot buyMarket,
        ExchangeMarketSnapshot sellMarket,
        decimal executableBase,
        AppSettings settings,
        decimal bufferBps)
    {
        if (healthFlags != DataHealthFlags.None)
        {
            return CreateRejectedEvaluation(mode, direction, testNotionalUsd, healthFlags, $"Market data degraded: {healthFlags}");
        }

        if (executableBase <= 0m)
        {
            return CreateRejectedEvaluation(mode, direction, testNotionalUsd, healthFlags, "Executable quantity rounded to zero");
        }

        var buySweep = _fillableSizeCalculator.SweepBase(buyMarket.OrderBook, executableBase, TradeSide.Buy);
        var sellSweep = _fillableSizeCalculator.SweepBase(sellMarket.OrderBook, executableBase, TradeSide.Sell);

        if (!buySweep.FullyFilled || !sellSweep.FullyFilled)
        {
            return CreateRejectedEvaluation(mode, direction, testNotionalUsd, healthFlags, "Insufficient depth for executable quantity");
        }

        var fillableQuantity = Math.Min(buySweep.ExecutedBaseQuantity, sellSweep.ExecutedBaseQuantity);
        var fillabilityStatus = ClassifyFillability(testNotionalUsd, buySweep.ExecutedQuoteQuantity, fillableQuantity);

        if (!SymbolRulesNormalizer.MeetsMinimums(buyMarket.Rules, buySweep.ExecutedBaseQuantity, buySweep.ExecutedQuoteQuantity) ||
            !SymbolRulesNormalizer.MeetsMinimums(sellMarket.Rules, sellSweep.ExecutedBaseQuantity, sellSweep.ExecutedQuoteQuantity))
        {
            return CreateRejectedEvaluation(mode, direction, testNotionalUsd, healthFlags, "Symbol minimums not met");
        }

        var buyFee = _feeCalculator.CalculateFeeUsd(buyMarket.Exchange, buySweep.ExecutedQuoteQuantity);
        var sellFee = _feeCalculator.CalculateFeeUsd(sellMarket.Exchange, sellSweep.ExecutedQuoteQuantity);
        var feesTotal = buyFee + sellFee;
        var grossPnl = sellSweep.ExecutedQuoteQuantity - buySweep.ExecutedQuoteQuantity;
        var bufferTotal = ComputeBufferUsd(bufferBps, buySweep.ExecutedQuoteQuantity, sellSweep.ExecutedQuoteQuantity, buyLiquidityDust: 0m);
        var netPnl = grossPnl - feesTotal - bufferTotal;
        var grossEdgePct = buySweep.ExecutedQuoteQuantity > 0m ? grossPnl / buySweep.ExecutedQuoteQuantity : 0m;
        var netEdgePct = buySweep.ExecutedQuoteQuantity > 0m ? netPnl / buySweep.ExecutedQuoteQuantity : 0m;
        var netRoiPct = netEdgePct * 100m;

        var buyLeg = new TradeLegEstimate(
            buyMarket.Exchange,
            TradeSide.Buy,
            buySweep.ExecutedBaseQuantity,
            buySweep.AveragePrice,
            buySweep.ExecutedQuoteQuantity,
            buyFee,
            buySweep.ExecutedQuoteQuantity + buyFee);

        var sellLeg = new TradeLegEstimate(
            sellMarket.Exchange,
            TradeSide.Sell,
            sellSweep.ExecutedBaseQuantity,
            sellSweep.AveragePrice,
            sellSweep.ExecutedQuoteQuantity,
            sellFee,
            sellSweep.ExecutedQuoteQuantity - sellFee);

        return new OpportunityEvaluation(
            mode,
            direction,
            testNotionalUsd,
            fillabilityStatus,
            executableBase,
            grossPnl,
            netPnl,
            grossEdgePct,
            netEdgePct,
            netRoiPct,
            feesTotal,
            bufferTotal,
            fillableQuantity,
            healthFlags,
            null,
            buyLeg,
            sellLeg);
    }

    private static OpportunityEvaluation CreateRejectedEvaluation(
        AnalysisMode mode,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        DataHealthFlags healthFlags,
        string reason) =>
        new(
            mode,
            direction,
            testNotionalUsd,
            FillabilityStatus.NotFillable,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            healthFlags,
            reason,
            null,
            null);

    private static FillabilityStatus ClassifyFillability(decimal requestedQuoteUsd, decimal buyQuoteUsd, decimal fillableQuantity)
    {
        if (fillableQuantity <= 0m)
        {
            return FillabilityStatus.NotFillable;
        }

        if (requestedQuoteUsd <= 0m)
        {
            return FillabilityStatus.NotFillable;
        }

        return buyQuoteUsd >= requestedQuoteUsd * 0.999m
            ? FillabilityStatus.Fillable
            : FillabilityStatus.PartiallyFillable;
    }

    private static decimal ComputeBufferUsd(decimal totalBufferBps, decimal buyCostUsd, decimal sellProceedsUsd, decimal buyLiquidityDust) =>
        ((buyCostUsd + sellProceedsUsd) * totalBufferBps / 10_000m) + buyLiquidityDust;
}
