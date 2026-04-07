using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class SummaryAndHealthGeneratorTests
{
    [Fact]
    public void SummaryGenerator_AggregatesSignalsAndWindows()
    {
        var generator = new SummaryGenerator();
        var fromUtc = new DateTimeOffset(2026, 04, 05, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddHours(1);

        var signals = new[]
        {
            CreateSignal(fromUtc.AddMinutes(1), ArbitrageDirection.BuyBinanceSellBybit, 100m, SignalClass.RawPositive, 0.05m, 5m),
            CreateSignal(fromUtc.AddMinutes(2), ArbitrageDirection.BuyBinanceSellBybit, 100m, SignalClass.FeePositive, 0.08m, 8m),
            CreateSignal(fromUtc.AddMinutes(3), ArbitrageDirection.BuyBybitSellBinance, 50m, SignalClass.NetPositive, 0.12m, 12m),
            CreateSignal(fromUtc.AddMinutes(4), ArbitrageDirection.BuyBybitSellBinance, 50m, SignalClass.EntryQualified, 0.20m, 20m)
        };

        var windows = new[]
        {
            new OpportunityWindowEvent("w1", "XRPUSDT", ArbitrageDirection.BuyBinanceSellBybit, 100m, fromUtc.AddMinutes(1), fromUtc.AddMinutes(2), 60000, 0.15m, 0.08m, 0.065m, 2, SignalClass.FeePositive),
            new OpportunityWindowEvent("w2", "XRPUSDT", ArbitrageDirection.BuyBybitSellBinance, 50m, fromUtc.AddMinutes(3), fromUtc.AddMinutes(5), 120000, 0.25m, 0.20m, 0.16m, 2, SignalClass.EntryQualified)
        };

        var summary = generator.Generate(SummaryPeriod.Hourly, fromUtc, toUtc, "XRPUSDT", signals, windows);

        Assert.Equal(4, summary.RawObservationCount);
        Assert.Equal(4, summary.RawPositiveSignalCount);
        Assert.Equal(3, summary.FeePositiveSignalCount);
        Assert.Equal(2, summary.NetPositiveSignalCount);
        Assert.Equal(1, summary.EntryQualifiedSignalCount);
        Assert.Equal(2, summary.TotalWindows);
        Assert.Equal(2, summary.WindowsByDirection.Count);
        Assert.Equal(2, summary.WindowsByNotional.Count);
        Assert.Equal(90000d, summary.AverageLifetimeMs);
        Assert.Equal(90000d, summary.MedianLifetimeMs);
        Assert.Equal(120000, summary.MaxLifetimeMs);
        Assert.Equal(0.20m, summary.TheoreticalNetPnlUsd);
        Assert.Contains("Положительных raw-сигналов: 4", summary.FinalAssessment, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthReportGenerator_CalculatesReconnectsErrorsAndUnhealthyDurations()
    {
        var generator = new HealthReportGenerator();
        var fromUtc = new DateTimeOffset(2026, 04, 05, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddMinutes(10);
        var startedAtUtc = fromUtc.AddMinutes(-5);

        var events = new[]
        {
            new HealthEvent(fromUtc.AddMinutes(1), HealthEventType.ExchangeError, ExchangeId.Binance, DataHealthFlags.BinanceUnhealthy, false, "Binance disconnected"),
            new HealthEvent(fromUtc.AddMinutes(3), HealthEventType.ExchangeRecovered, ExchangeId.Binance, DataHealthFlags.None, true, "Binance recovered"),
            new HealthEvent(fromUtc.AddMinutes(4), HealthEventType.ExchangeRecovered, ExchangeId.Bybit, DataHealthFlags.None, true, "Bybit reconnected"),
            new HealthEvent(fromUtc.AddMinutes(6), HealthEventType.ExchangeError, ExchangeId.Bybit, DataHealthFlags.BybitUnhealthy, false, "Bybit error"),
            new HealthEvent(fromUtc.AddMinutes(8), HealthEventType.ExchangeRecovered, ExchangeId.Bybit, DataHealthFlags.None, true, "Bybit healthy again")
        };

        var report = generator.Generate(SummaryPeriod.Hourly, fromUtc, toUtc, "XRPUSDT", startedAtUtc, events);

        Assert.Equal(900000, report.UptimeMs);
        Assert.Equal(3, report.ReconnectCount);
        Assert.Equal(240000, report.TotalDegradedDurationMs);
        Assert.Equal(2, report.MarketDataErrorCount);
        Assert.Equal(120000, report.UnhealthyDurationMsByExchange["Binance"]);
        Assert.Equal(120000, report.UnhealthyDurationMsByExchange["Bybit"]);
    }

    private static RawSignalEvent CreateSignal(
        DateTimeOffset timestampUtc,
        ArbitrageDirection direction,
        decimal testNotionalUsd,
        SignalClass signalClass,
        decimal netEdgeUsd,
        decimal netEdgeBps) =>
        new(
            timestampUtc,
            "XRPUSDT",
            direction,
            testNotionalUsd,
            0.999m,
            1.000m,
            1.004m,
            1.005m,
            0.25m,
            25m,
            0.10m,
            0.10m,
            0.20m,
            0.01m,
            netEdgeUsd,
            netEdgeBps,
            signalClass == SignalClass.EntryQualified ? netEdgeUsd : 0m,
            signalClass == SignalClass.EntryQualified ? 0.2m : 0m,
            signalClass,
            DataHealthFlags.None);
}
