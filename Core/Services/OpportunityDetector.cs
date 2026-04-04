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
            return new OpportunityPairEvaluation(
                snapshot.CapturedAtUtc,
                direction,
                testNotionalUsd,
                empty with { Mode = AnalysisMode.Optimistic },
                empty,
                0m,
                0m,
                0m,
                0m,
                empty.HealthFlags,
                BuildMissingMarketFillabilityDecision(testNotionalUsd));
        }

        var healthFlags = snapshot.HealthFlags;
        var buyBestAsk = buyMarket.OrderBook.BestAsk?.Price ?? 0m;
        var buyTopQuantity = buyMarket.OrderBook.BestAsk?.Quantity ?? 0m;
        var sellTopQuantity = sellMarket.OrderBook.BestBid?.Quantity ?? 0m;
        var requiredBaseQuantity = buyBestAsk > 0m ? testNotionalUsd / buyBestAsk : 0m;
        var buyLiquidityFromQuote = _fillableSizeCalculator.SweepQuote(buyMarket.OrderBook, testNotionalUsd, TradeSide.Buy);
        var sellLiquidityBase = _fillableSizeCalculator.GetAvailableBaseLiquidity(sellMarket.OrderBook, TradeSide.Sell);

        var quantityBeforeRounding = Math.Min(buyLiquidityFromQuote.ExecutedBaseQuantity, sellLiquidityBase);
        var roundedForBinance = SymbolRulesNormalizer.RoundDownToStep(quantityBeforeRounding, snapshot.Binance?.Rules.QuantityStep ?? 0m);
        var roundedForBybit = SymbolRulesNormalizer.RoundDownToStep(quantityBeforeRounding, snapshot.Bybit?.Rules.QuantityStep ?? 0m);
        var executableBase = quantityBeforeRounding;
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

        var fillabilityDecision = BuildFillabilityDecision(
            testNotionalUsd,
            requiredBaseQuantity,
            buyTopQuantity,
            sellTopQuantity,
            buyLiquidityFromQuote,
            sellLiquidityBase,
            quantityBeforeRounding,
            roundedForBinance,
            roundedForBybit,
            executableBase,
            buyMarket,
            sellMarket);

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
            healthFlags,
            fillabilityDecision);
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

    private FillabilityDecisionDetails BuildFillabilityDecision(
        decimal testNotionalUsd,
        decimal requiredBaseQuantity,
        decimal buyTopQuantity,
        decimal sellTopQuantity,
        SweepResult buyLiquidityFromQuote,
        decimal sellLiquidityBase,
        decimal quantityBeforeRounding,
        decimal roundedForBinance,
        decimal roundedForBybit,
        decimal executableBase,
        ExchangeMarketSnapshot buyMarket,
        ExchangeMarketSnapshot sellMarket)
    {
        var buySweep = executableBase > 0m
            ? _fillableSizeCalculator.SweepBase(buyMarket.OrderBook, executableBase, TradeSide.Buy)
            : new SweepResult(TradeSide.Buy, executableBase, 0m, 0m, 0m, buyMarket.OrderBook.BestAsk?.Price, false, 0);

        var sellSweep = executableBase > 0m
            ? _fillableSizeCalculator.SweepBase(sellMarket.OrderBook, executableBase, TradeSide.Sell)
            : new SweepResult(TradeSide.Sell, executableBase, 0m, 0m, 0m, sellMarket.OrderBook.BestBid?.Price, false, 0);

        var decisionCode = "fillable";
        var decisionSummary = "Requested notional remains executable after depth sweep and exchange rounding.";
        string? detail = null;

        if (!buyLiquidityFromQuote.FullyFilled)
        {
            decisionCode = "buy_quote_depth_insufficient";
            decisionSummary = "Buy-side ask depth cannot fully consume the requested quote notional.";
            detail = "Requested quote notional exceeds visible buy-side depth.";
        }
        else if (sellLiquidityBase <= 0m)
        {
            decisionCode = "sell_liquidity_unavailable";
            decisionSummary = "Sell-side bid depth has no executable base liquidity.";
            detail = "No positive bid quantity was available on the sell venue.";
        }
        else if (quantityBeforeRounding <= 0m)
        {
            decisionCode = "pre_rounding_quantity_zero";
            decisionSummary = "No common executable quantity exists before exchange rule rounding.";
            detail = "The minimum of buy-side and sell-side liquidity collapsed to zero.";
        }
        else if (executableBase <= 0m)
        {
            decisionCode = "rounded_below_exchange_minimum";
            decisionSummary = "Exchange quantity rules rounded the executable size below the tradable minimum.";
            detail = "Shared executable quantity becomes zero after applying Binance/Bybit quantity steps and minimums.";
        }
        else if (!buySweep.FullyFilled)
        {
            decisionCode = "buy_depth_insufficient_after_rounding";
            decisionSummary = "Buy-side depth cannot support the rounded executable quantity.";
            detail = "Rounded executable quantity requires more ask depth than was available.";
        }
        else if (!sellSweep.FullyFilled)
        {
            decisionCode = "sell_depth_insufficient_after_rounding";
            decisionSummary = "Sell-side depth cannot support the rounded executable quantity.";
            detail = "Rounded executable quantity requires more bid depth than was available.";
        }
        else if (buySweep.ExecutedQuoteQuantity < testNotionalUsd * 0.999m)
        {
            decisionCode = "quote_shortfall_after_rounding";
            decisionSummary = "Rounded executable quantity no longer covers the requested quote notional.";
            detail = "The trade remains only partially fillable after exchange-specific quantity normalization.";
        }

        return new FillabilityDecisionDetails(
            decisionCode,
            decisionSummary,
            testNotionalUsd,
            requiredBaseQuantity,
            buyTopQuantity,
            sellTopQuantity,
            Math.Min(buyTopQuantity, sellTopQuantity),
            buyLiquidityFromQuote.ExecutedBaseQuantity,
            sellLiquidityBase,
            Math.Min(buyLiquidityFromQuote.ExecutedBaseQuantity, sellLiquidityBase),
            quantityBeforeRounding,
            roundedForBinance,
            roundedForBybit,
            executableBase,
            buySweep.ExecutedBaseQuantity,
            sellSweep.ExecutedBaseQuantity,
            buySweep.FullyFilled,
            sellSweep.FullyFilled,
            buyLiquidityFromQuote.ConsumedLevels,
            buySweep.ConsumedLevels,
            sellSweep.ConsumedLevels,
            detail);
    }

    private static FillabilityDecisionDetails BuildMissingMarketFillabilityDecision(decimal testNotionalUsd) =>
        new(
            "missing_market_snapshot",
            "One or both exchange snapshots are missing, so fillability cannot be evaluated.",
            testNotionalUsd,
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
            false,
            false,
            0,
            0,
            0,
            "Missing exchange snapshot.");
}
