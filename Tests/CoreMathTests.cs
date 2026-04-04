using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class CoreMathTests
{
    [Fact]
    public void FeeCalculator_UsesExchangeSpecificRate()
    {
        var calculator = new FeeCalculator(0.001m, 0.002m);

        Assert.Equal(0.1m, calculator.CalculateFeeUsd(ExchangeId.Binance, 100m));
        Assert.Equal(0.2m, calculator.CalculateFeeUsd(ExchangeId.Bybit, 100m));
    }

    [Fact]
    public void SymbolRulesNormalizer_RoundsDownToBothExchangeSteps()
    {
        var buyRules = new ExchangeSymbolRules(ExchangeId.Binance, "TRXUSDT", "TRX", "USDT", 0.1m, 1m, 10_000m, 0.0001m, 5m);
        var sellRules = new ExchangeSymbolRules(ExchangeId.Bybit, "TRXUSDT", "TRX", "USDT", 0.25m, 1m, 10_000m, 0.0001m, 5m);

        var executable = SymbolRulesNormalizer.GetExecutableQuantity(12.87m, buyRules, sellRules);

        Assert.Equal(12.75m, executable);
    }

    [Fact]
    public void FillableSizeCalculator_SweepsAcrossMultipleLevels()
    {
        var calculator = new FillableSizeCalculator();
        var book = new OrderBookSnapshot(
            "TRXUSDT",
            OrderBookSyncStatus.Synced,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            [new OrderBookLevel(0.249m, 100m), new OrderBookLevel(0.248m, 100m)],
            [new OrderBookLevel(0.250m, 100m), new OrderBookLevel(0.251m, 100m)]);

        var result = calculator.SweepQuote(book, 30m, TradeSide.Buy);

        Assert.True(result.FullyFilled);
        Assert.True(result.ExecutedBaseQuantity > 119m);
        Assert.Equal(2, result.ConsumedLevels);
    }

    [Fact]
    public void OpportunityDetector_CalculatesPositiveConservativePnl()
    {
        var detector = new OpportunityDetector(new FeeCalculator(0.001m, 0.001m), new FillableSizeCalculator());
        var settings = CreateSettings();
        var snapshot = new MarketDataSnapshot(
            "TRXUSDT",
            DateTimeOffset.UtcNow,
            CreateExchangeSnapshot(ExchangeId.Binance, bid: 0.255m, ask: 0.250m),
            CreateExchangeSnapshot(ExchangeId.Bybit, bid: 0.260m, ask: 0.256m),
            DataHealthFlags.None);

        var result = detector.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, 20m, settings);

        Assert.Equal(FillabilityStatus.Fillable, result.Conservative.FillabilityStatus);
        Assert.True(result.Conservative.GrossPnlUsd > 0m);
        Assert.True(result.Conservative.NetPnlUsd > 0m);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public void OpportunityDetector_XrpDepthAndRoundingRemainFillableForSmallNotionals(decimal testNotionalUsd)
    {
        var detector = new OpportunityDetector(new FeeCalculator(0.001m, 0.001m), new FillableSizeCalculator());
        var settings = CreateSettings();
        var snapshot = new MarketDataSnapshot(
            "XRPUSDT",
            DateTimeOffset.UtcNow,
            CreateExchangeSnapshot(ExchangeId.Binance, bid: 2.0500m, ask: 2.0000m, symbol: "XRPUSDT", baseAsset: "XRP", quantityStep: 1m, minQuantity: 1m, bidQuantity: 20_000m, askQuantity: 20_000m),
            CreateExchangeSnapshot(ExchangeId.Bybit, bid: 2.0600m, ask: 2.0100m, symbol: "XRPUSDT", baseAsset: "XRP", quantityStep: 0.1m, minQuantity: 0.1m, bidQuantity: 25_000m, askQuantity: 25_000m),
            DataHealthFlags.None);

        var result = detector.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, testNotionalUsd, settings);

        Assert.Equal(FillabilityStatus.Fillable, result.Conservative.FillabilityStatus);
        Assert.True(result.FillabilityDecision.RequiredBaseQuantity > 0m);
        Assert.True(result.FillabilityDecision.QuantityBeforeRounding >= result.FillabilityDecision.EffectiveExecutableQuantity);
        Assert.True(result.FillabilityDecision.EffectiveExecutableQuantity > 0m);
        Assert.Equal("fillable", result.FillabilityDecision.DecisionCode);
    }

    [Fact]
    public void OpportunityDetector_XrpWholeUnitRoundingKeepsExecutableQuantityAboveMinimum()
    {
        var detector = new OpportunityDetector(new FeeCalculator(0.001m, 0.001m), new FillableSizeCalculator());
        var settings = CreateSettings();
        var snapshot = new MarketDataSnapshot(
            "XRPUSDT",
            DateTimeOffset.UtcNow,
            CreateExchangeSnapshot(ExchangeId.Binance, bid: 2.0500m, ask: 1.9995m, symbol: "XRPUSDT", baseAsset: "XRP", quantityStep: 1m, minQuantity: 1m, bidQuantity: 10_000m, askQuantity: 10_000m),
            CreateExchangeSnapshot(ExchangeId.Bybit, bid: 2.0600m, ask: 2.0100m, symbol: "XRPUSDT", baseAsset: "XRP", quantityStep: 0.1m, minQuantity: 0.1m, bidQuantity: 10_000m, askQuantity: 10_000m),
            DataHealthFlags.None);

        var result = detector.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, 10m, settings);

        Assert.Equal(5m, result.FillabilityDecision.QuantityAfterRoundingBinanceRules);
        Assert.Equal(5m, result.FillabilityDecision.EffectiveExecutableQuantity);
        Assert.Equal(FillabilityStatus.Fillable, result.Conservative.FillabilityStatus);
    }

    [Fact]
    public void OpportunityLifetimeTracker_ClosesWindowWhenEdgeDisappears()
    {
        var settings = CreateSettings(minWindowLifetimeMs: 500);
        var tracker = new OpportunityLifetimeTracker();
        var open = CreateEvaluation(DateTimeOffset.UtcNow, 1.5m);
        var close = CreateEvaluation(DateTimeOffset.UtcNow.AddMilliseconds(800), -0.1m);

        var duringOpen = tracker.Process("TRXUSDT", open, settings);
        var onClose = tracker.Process("TRXUSDT", close, settings);

        Assert.Empty(duringOpen.ClosedWindows);
        Assert.Single(onClose.ClosedWindows);
        Assert.True(onClose.ClosedWindows[0].LifetimeMs >= 800);
    }

    [Fact]
    public void SummaryGenerator_AggregatesWindowStatistics()
    {
        var generator = new SummaryGenerator();
        var fromUtc = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddHours(1);
        var windows = new[]
        {
            new OpportunityWindowEvent("w1", fromUtc.AddMinutes(1), fromUtc.AddMinutes(2), 60_000, "TRXUSDT", ArbitrageDirection.BuyBinanceSellBybit, "Binance", "Bybit", 10m, FillabilityStatus.Fillable, 40m, 40m, 0.25m, 0.251m, 0.26m, 0.261m, 0.5m, 0.3m, 0.2m, 0.02m, 0.015m, 0.01m, 0.05m, 0.02m, DataHealthFlags.None, null),
            new OpportunityWindowEvent("w2", fromUtc.AddMinutes(10), fromUtc.AddMinutes(12), 120_000, "TRXUSDT", ArbitrageDirection.BuyBybitSellBinance, "Bybit", "Binance", 20m, FillabilityStatus.PartiallyFillable, 70m, 70m, 0.25m, 0.251m, 0.26m, 0.261m, -0.1m, 0.05m, -0.2m, -0.005m, 0.002m, -0.01m, 0.06m, 0.03m, DataHealthFlags.Degraded, null)
        };
        var health = new[]
        {
            new HealthEvent(fromUtc, HealthEventType.OverallHealthChanged, null, DataHealthFlags.None, true, "Healthy"),
            new HealthEvent(fromUtc.AddMinutes(30), HealthEventType.OverallHealthChanged, null, DataHealthFlags.Degraded, false, "Degraded")
        };

        var telemetry = new[]
        {
            new EvaluationTelemetrySnapshot(fromUtc.AddMinutes(5), "TRXUSDT", ArbitrageDirection.BuyBinanceSellBybit, 10m, true, false, true, FillabilityStatus.PartiallyFillable, 0.2m, 0.01m, 0.005m, 0.03m, 0.01m, -0.02m, -0.002m, 40m, DataHealthFlags.None, ["fillability", "fees"], "fillability", ["fees"], false, true, 0.25m, 0.251m, 0.26m, 0.261m, CreateFillabilityDecisionDetails("quote_shortfall_after_rounding", 10m, 40m)),
            new EvaluationTelemetrySnapshot(fromUtc.AddMinutes(15), "TRXUSDT", ArbitrageDirection.BuyBybitSellBinance, 20m, true, false, false, FillabilityStatus.NotFillable, 0.1m, 0.006m, 0.006m, 0.2m, 0.01m, -0.11m, -0.011m, 10m, DataHealthFlags.Degraded, ["health", "rules", "min_lifetime"], "health", ["rules", "min_lifetime"], false, false, 0.25m, 0.251m, 0.26m, 0.261m, CreateFillabilityDecisionDetails("rounded_below_exchange_minimum", 20m, 10m)),
            new EvaluationTelemetrySnapshot(fromUtc.AddMinutes(20), "TRXUSDT", ArbitrageDirection.BuyBinanceSellBybit, 10m, true, true, true, FillabilityStatus.Fillable, 0.3m, 0.015m, 0.014m, 0.01m, 0.01m, 0.28m, 0.014m, 50m, DataHealthFlags.None, [], null, [], false, false, 0.25m, 0.251m, 0.26m, 0.261m, CreateFillabilityDecisionDetails("fillable", 10m, 50m))
        };

        var summary = generator.Generate(SummaryPeriod.Hourly, fromUtc, toUtc, "TRXUSDT", windows, health, telemetry);

        Assert.Equal(2, summary.TotalWindows);
        Assert.Equal("TRXUSDT", summary.Symbol);
        Assert.Equal(1, summary.FillableCount);
        Assert.Equal(1, summary.PartiallyFillableCount);
        Assert.Equal(0m, Math.Round(summary.TotalNetPnlUsd, 1));
        Assert.True(summary.HealthyDurationMs > 0);
        Assert.True(summary.DegradedDurationMs > 0);
        Assert.Equal(3, summary.DebugStats.RawPositiveCrossCount);
        Assert.Equal(1, summary.DebugStats.RejectedDueToMinLifetimeCount);
        Assert.Equal(2, summary.DebugStats.RejectedDueToMultipleReasonsCount);
        Assert.Equal(1, summary.DebugStats.RejectedDueToFeesAndFillabilityCount);
        Assert.Equal(1, summary.DebugStats.WouldBeProfitableWithoutFillabilityCount);
        Assert.Equal(2, summary.DebugStats.RawPositiveCrossCountByDirection["BuyBinanceSellBybit"]);
        Assert.Equal(1, summary.DebugStats.RejectReasonCountsByNotional["10:fees"]);
        Assert.Equal(1, summary.DebugStats.PrimaryRejectReasonCounts["fillability"]);
        Assert.Equal(1, summary.FillabilityDiagnostics.FillableCountByNotional["10"]);
    }

    private static AppSettings CreateSettings(int minWindowLifetimeMs = 1_000) =>
        new()
        {
            Symbol = "TRXUSDT",
            BaseAsset = "TRX",
            QuoteAsset = "USDT",
            MinWindowLifetimeMs = minWindowLifetimeMs,
            TestNotionalsUsd = [10m, 20m],
            BinanceTakerFeeRate = 0.001m,
            BybitTakerFeeRate = 0.001m,
            Buffers = new BufferSettings
            {
                LatencyBufferBps = 1m,
                SlippageBufferBps = 1m,
                AdditionalSafetyBufferBps = 1m
            },
            Thresholds = new ThresholdSettings
            {
                EntryThresholdUsd = 0m,
                EntryThresholdBps = 0m
            }
        };

    private static ExchangeMarketSnapshot CreateExchangeSnapshot(
        ExchangeId exchange,
        decimal bid,
        decimal ask,
        string symbol = "TRXUSDT",
        string baseAsset = "TRX",
        decimal quantityStep = 0.1m,
        decimal minQuantity = 1m,
        decimal bidQuantity = 1_000m,
        decimal askQuantity = 1_000m)
    {
        var rules = new ExchangeSymbolRules(exchange, symbol, baseAsset, "USDT", quantityStep, minQuantity, 10_000m, 0.0001m, 5m);
        var book = new OrderBookSnapshot(
            symbol,
            OrderBookSyncStatus.Synced,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            [new OrderBookLevel(bid, bidQuantity), new OrderBookLevel(bid - 0.001m, bidQuantity)],
            [new OrderBookLevel(ask, askQuantity), new OrderBookLevel(ask + 0.001m, askQuantity)]);

        return new ExchangeMarketSnapshot(exchange, rules, book);
    }

    private static OpportunityPairEvaluation CreateEvaluation(DateTimeOffset timestampUtc, decimal conservativeNetPnlUsd)
    {
        var profitable = conservativeNetPnlUsd > 0m;
        var conservative = new OpportunityEvaluation(
            AnalysisMode.Conservative,
            ArbitrageDirection.BuyBinanceSellBybit,
            10m,
            profitable ? FillabilityStatus.Fillable : FillabilityStatus.NotFillable,
            40m,
            1m,
            conservativeNetPnlUsd,
            0.02m,
            0.01m,
            1m,
            0.02m,
            0.01m,
            40m,
            profitable ? DataHealthFlags.None : DataHealthFlags.Degraded,
            profitable ? null : "Closed",
            new TradeLegEstimate(ExchangeId.Binance, TradeSide.Buy, 40m, 0.25m, 10m, 0.01m, 10.01m),
            new TradeLegEstimate(ExchangeId.Bybit, TradeSide.Sell, 40m, 0.26m, 10.4m, 0.01m, 10.39m));
        var optimistic = conservative with
        {
            Mode = AnalysisMode.Optimistic,
            NetPnlUsd = conservativeNetPnlUsd + 0.05m,
            NetEdgePct = conservative.NetEdgePct + 0.001m
        };

        return new OpportunityPairEvaluation(timestampUtc, ArbitrageDirection.BuyBinanceSellBybit, 10m, optimistic, conservative, 0.25m, 0.251m, 0.26m, 0.261m, conservative.HealthFlags, CreateFillabilityDecisionDetails("fillable", 10m, 40m));
    }

    private static FillabilityDecisionDetails CreateFillabilityDecisionDetails(string decisionCode, decimal requestedQuoteUsd, decimal executableQuantity) =>
        new(
            decisionCode,
            decisionCode,
            requestedQuoteUsd,
            executableQuantity,
            executableQuantity * 2m,
            executableQuantity * 2m,
            executableQuantity * 2m,
            executableQuantity * 3m,
            executableQuantity * 3m,
            executableQuantity * 3m,
            executableQuantity,
            executableQuantity,
            executableQuantity,
            executableQuantity,
            executableQuantity,
            executableQuantity,
            decisionCode == "fillable",
            decisionCode == "fillable",
            1,
            1,
            1,
            null);
}
