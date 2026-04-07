using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class SignalCalculatorTests
{
    private static readonly AppSettings DefaultSettings = new()
    {
        Symbol = "XRPUSDT",
        BaseAsset = "XRP",
        QuoteAsset = "USDT",
        TestNotionalsUsd = [100m],
        BinanceTakerFeeRate = 0.001m,
        BybitTakerFeeRate = 0.001m,
        SafetyBufferBps = 2m,
        EntryThresholdUsd = 0.05m,
        EntryThresholdBps = 3m
    };

    [Fact]
    public void Evaluate_ProducesEntryQualifiedSignal_WhenNetEdgeClearsThresholds()
    {
        var calculator = new SignalCalculator(new FeeCalculator(0.001m, 0.001m));
        var snapshot = CreateSnapshot(binanceAsk: 1.0000m, binanceBid: 0.9995m, bybitAsk: 1.0020m, bybitBid: 1.0060m);

        var evaluation = calculator.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, 100m, DefaultSettings);

        Assert.True(evaluation.IsQuoteUsable);
        Assert.Equal(SignalClass.EntryQualified, evaluation.SignalClass);
        Assert.Equal(100m, evaluation.BaseQuantity);
        Assert.Equal(0.6m, evaluation.GrossSpreadUsd);
        Assert.Equal(0.2006m, evaluation.TotalFeesUsd);
        Assert.Equal(0.04012m, evaluation.SafetyBufferUsd);
        Assert.Equal(0.35928m, evaluation.NetEdgeUsd);
        Assert.Equal(35.928m, evaluation.NetEdgeBps);
        Assert.Equal(0.35928m, evaluation.ExpectedNetPnlUsd);
        Assert.Equal(0.35928m, evaluation.ExpectedNetPnlPct);
    }

    [Fact]
    public void Evaluate_StopsAtFeePositive_WhenSafetyBufferTurnsEdgeNegative()
    {
        var calculator = new SignalCalculator(new FeeCalculator(0.001m, 0.001m));
        var settings = new AppSettings
        {
            Symbol = DefaultSettings.Symbol,
            BaseAsset = DefaultSettings.BaseAsset,
            QuoteAsset = DefaultSettings.QuoteAsset,
            ScanIntervalMs = DefaultSettings.ScanIntervalMs,
            RestHealthProbeEnabled = DefaultSettings.RestHealthProbeEnabled,
            RestHealthProbeAfterMs = DefaultSettings.RestHealthProbeAfterMs,
            RestHealthProbeCooldownMs = DefaultSettings.RestHealthProbeCooldownMs,
            CumulativeSummaryIntervalSeconds = DefaultSettings.CumulativeSummaryIntervalSeconds,
            TestNotionalsUsd = DefaultSettings.TestNotionalsUsd,
            BinanceTakerFeeRate = DefaultSettings.BinanceTakerFeeRate,
            BybitTakerFeeRate = DefaultSettings.BybitTakerFeeRate,
            SafetyBufferBps = 10m,
            EntryThresholdUsd = 0m,
            EntryThresholdBps = 0m,
            Storage = DefaultSettings.Storage,
            Binance = DefaultSettings.Binance,
            Bybit = DefaultSettings.Bybit
        };
        var snapshot = CreateSnapshot(binanceAsk: 1.0000m, binanceBid: 0.9995m, bybitAsk: 1.0015m, bybitBid: 1.0025m);

        var evaluation = calculator.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, 100m, settings);

        Assert.Equal(SignalClass.FeePositive, evaluation.SignalClass);
        Assert.True(evaluation.GrossSpreadUsd > 0m);
        Assert.True(evaluation.GrossSpreadUsd - evaluation.TotalFeesUsd > 0m);
        Assert.True(evaluation.NetEdgeUsd < 0m);
    }

    [Fact]
    public void Evaluate_ReturnsNonPositive_WhenQuotesAreMissing()
    {
        var calculator = new SignalCalculator(new FeeCalculator(0.001m, 0.001m));
        var snapshot = new MarketDataSnapshot(
            "XRPUSDT",
            DateTimeOffset.UtcNow,
            new ExchangeMarketSnapshot(ExchangeId.Binance, "XRPUSDT", DateTimeOffset.UtcNow, null, null, null, null, null, TimeSpan.MaxValue, false, 0),
            new ExchangeMarketSnapshot(ExchangeId.Bybit, "XRPUSDT", DateTimeOffset.UtcNow, 1.001m, 1000m, 1.002m, 1000m, DateTimeOffset.UtcNow, TimeSpan.Zero, true, 0),
            DataHealthFlags.BinanceMissing | DataHealthFlags.BinanceUnhealthy);

        var evaluation = calculator.Evaluate(snapshot, ArbitrageDirection.BuyBinanceSellBybit, 100m, DefaultSettings);

        Assert.False(evaluation.IsQuoteUsable);
        Assert.Equal(SignalClass.NonPositive, evaluation.SignalClass);
        Assert.Equal(0m, evaluation.NetEdgeUsd);
        Assert.Equal(DataHealthFlags.BinanceMissing | DataHealthFlags.BinanceUnhealthy, evaluation.HealthFlags);
    }

    private static MarketDataSnapshot CreateSnapshot(decimal binanceAsk, decimal binanceBid, decimal bybitAsk, decimal bybitBid)
    {
        var now = DateTimeOffset.UtcNow;
        return new MarketDataSnapshot(
            "XRPUSDT",
            now,
            new ExchangeMarketSnapshot(ExchangeId.Binance, "XRPUSDT", now, binanceBid, 5000m, binanceAsk, 5000m, now, TimeSpan.Zero, true, 0),
            new ExchangeMarketSnapshot(ExchangeId.Bybit, "XRPUSDT", now, bybitBid, 6000m, bybitAsk, 6000m, now, TimeSpan.Zero, true, 0),
            DataHealthFlags.None);
    }
}
