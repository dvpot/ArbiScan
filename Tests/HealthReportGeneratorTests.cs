using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class HealthReportGeneratorTests
{
    [Fact]
    public void Generate_AggregatesCountsDurationsAndLongestStaleIntervals()
    {
        var generator = new HealthReportGenerator();
        var fromUtc = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddMinutes(10);
        var healthEvents = new[]
        {
            new HealthEvent(fromUtc.AddMinutes(1), HealthEventType.ReconnectStarted, ExchangeId.Bybit, DataHealthFlags.Degraded, false, "Bybit reconnect started"),
            new HealthEvent(fromUtc.AddMinutes(2), HealthEventType.ReconnectCompleted, ExchangeId.Bybit, DataHealthFlags.None, true, "Bybit reconnect completed"),
            new HealthEvent(fromUtc.AddMinutes(3), HealthEventType.StaleQuotesDetected, ExchangeId.Bybit, DataHealthFlags.BybitStale | DataHealthFlags.Degraded, false, "Bybit stale"),
            new HealthEvent(fromUtc.AddMinutes(5), HealthEventType.StaleQuotesRecovered, ExchangeId.Bybit, DataHealthFlags.None, true, "Recovered"),
            new HealthEvent(fromUtc.AddMinutes(7), HealthEventType.ResyncStarted, ExchangeId.Binance, DataHealthFlags.Degraded, false, "Binance resync started"),
            new HealthEvent(fromUtc.AddMinutes(8), HealthEventType.ResyncCompleted, ExchangeId.Binance, DataHealthFlags.None, true, "Binance resync completed")
        };
        var diagnostics = new[]
        {
            new StaleDiagnosticEvent(fromUtc.AddMinutes(3), ExchangeId.Bybit, "stale_detected", "TRXUSDT", 2_000, 500, 250, OrderBookSyncStatus.Synced, fromUtc.AddMinutes(3), fromUtc.AddMinutes(2).AddSeconds(58), fromUtc.AddMinutes(2).AddSeconds(58), fromUtc.AddMinutes(2).AddSeconds(57), 2_100, 2_100, 3_000, 3_000, 2_800, 0.26m, 0.261m, 50, 50, 1_000m, 1_000m, fromUtc.AddMinutes(3), fromUtc.AddMinutes(3).AddMilliseconds(12), 12, Environment.CurrentManagedThreadId, false, ["BybitStale", "Degraded"], "callback_gap"),
            new StaleDiagnosticEvent(fromUtc.AddMinutes(5), ExchangeId.Bybit, "stale_recovered", "TRXUSDT", 2_000, 500, 250, OrderBookSyncStatus.Synced, fromUtc.AddMinutes(5), fromUtc.AddMinutes(4).AddSeconds(59), fromUtc.AddMinutes(4).AddSeconds(59), fromUtc.AddMinutes(4).AddSeconds(59), 50, 50, 40, 40, 500, 0.26m, 0.261m, 50, 50, 1_000m, 1_000m, fromUtc.AddMinutes(5), fromUtc.AddMinutes(5).AddMilliseconds(8), 8, Environment.CurrentManagedThreadId, true, [], "unknown")
        };

        var report = generator.Generate(SummaryPeriod.Hourly, fromUtc, toUtc, "TRXUSDT", healthEvents, diagnostics);

        Assert.Equal(1, report.ReconnectCountByExchange["Bybit"]);
        Assert.Equal(1, report.ResyncCountByExchange["Binance"]);
        Assert.Equal(1, report.StaleCountByExchange["Bybit"]);
        Assert.Equal(1, report.StaleDetectedCountByExchange["Bybit"]);
        Assert.Equal(1, report.StaleRecoveredCountByExchange["Bybit"]);
        Assert.Equal(120_000, report.LongestStaleIntervalMsByExchange["Bybit"]);
        Assert.True(report.DegradedDurationMs > 0);
        Assert.Equal(fromUtc.AddMinutes(3), report.LastStaleDetectedAtUtcByExchange["Bybit"]);
        Assert.Equal(fromUtc.AddMinutes(1), report.LastReconnectAtUtcByExchange["Bybit"]);
        Assert.Equal(fromUtc.AddMinutes(7), report.LastResyncAtUtcByExchange["Binance"]);
        Assert.Contains("BybitStale", report.TopDegradationCauses.Keys);
        Assert.Equal("callback_gap", report.StaleLikelyRootCauseByExchange["Bybit"]);
        Assert.True(report.MaxCallbackSilenceMsByExchange["Bybit"] >= 3_000);
    }
}
