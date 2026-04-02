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
            new HealthEvent(fromUtc.AddMinutes(3), HealthEventType.StaleQuotesDetected, null, DataHealthFlags.BybitStale | DataHealthFlags.Degraded, false, "Bybit stale"),
            new HealthEvent(fromUtc.AddMinutes(5), HealthEventType.StaleQuotesRecovered, null, DataHealthFlags.None, true, "Recovered"),
            new HealthEvent(fromUtc.AddMinutes(7), HealthEventType.ResyncStarted, ExchangeId.Binance, DataHealthFlags.Degraded, false, "Binance resync started"),
            new HealthEvent(fromUtc.AddMinutes(8), HealthEventType.ResyncCompleted, ExchangeId.Binance, DataHealthFlags.None, true, "Binance resync completed")
        };

        var report = generator.Generate(SummaryPeriod.Hourly, fromUtc, toUtc, "TRXUSDT", healthEvents);

        Assert.Equal(1, report.ReconnectCountByExchange["Bybit"]);
        Assert.Equal(1, report.ResyncCountByExchange["Binance"]);
        Assert.Equal(1, report.StaleCountByExchange["Bybit"]);
        Assert.Equal(120_000, report.LongestStaleIntervalMsByExchange["Bybit"]);
        Assert.True(report.DegradedDurationMs > 0);
        Assert.Equal(fromUtc.AddMinutes(3), report.LastStaleDetectedAtUtcByExchange["Bybit"]);
        Assert.Equal(fromUtc.AddMinutes(1), report.LastReconnectAtUtcByExchange["Bybit"]);
        Assert.Equal(fromUtc.AddMinutes(7), report.LastResyncAtUtcByExchange["Binance"]);
        Assert.Contains("BybitStale", report.TopDegradationCauses.Keys);
    }
}
