using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class OpportunityLifetimeTrackerTests
{
    [Fact]
    public void Process_ClosesWindow_WhenSignalBecomesNonPositive()
    {
        var tracker = new OpportunityLifetimeTracker();
        var openedAt = new DateTimeOffset(2026, 04, 05, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(tracker.Process(CreateSignal(openedAt, 0.15m, SignalClass.RawPositive)));
        Assert.Empty(tracker.Process(CreateSignal(openedAt.AddMilliseconds(250), 0.35m, SignalClass.EntryQualified)));

        var windows = tracker.Process(CreateSignal(openedAt.AddMilliseconds(750), -0.01m, SignalClass.NonPositive));

        var window = Assert.Single(windows);
        Assert.Equal("XRPUSDT", window.Symbol);
        Assert.Equal(ArbitrageDirection.BuyBinanceSellBybit, window.Direction);
        Assert.Equal(100m, window.TestNotionalUsd);
        Assert.Equal(750, window.DurationMs);
        Assert.Equal(0.45m, window.MaxGrossSpreadUsd);
        Assert.Equal(0.35m, window.MaxNetEdgeUsd);
        Assert.Equal(0.25m, window.AverageNetEdgeUsd);
        Assert.Equal(2, window.ObservationCount);
        Assert.Equal(SignalClass.EntryQualified, window.FinalWindowClass);
    }

    [Fact]
    public void FlushAll_ClosesStillOpenWindows()
    {
        var tracker = new OpportunityLifetimeTracker();
        var openedAt = new DateTimeOffset(2026, 04, 05, 1, 0, 0, TimeSpan.Zero);

        tracker.Process(CreateSignal(openedAt, 0.10m, SignalClass.NetPositive));

        var flushed = tracker.FlushAll(openedAt.AddSeconds(2));

        var window = Assert.Single(flushed);
        Assert.Equal(2000, window.DurationMs);
        Assert.Equal(SignalClass.NetPositive, window.FinalWindowClass);
    }

    private static RawSignalEvent CreateSignal(DateTimeOffset timestampUtc, decimal netEdgeUsd, SignalClass signalClass) =>
        new(
            timestampUtc,
            "XRPUSDT",
            ArbitrageDirection.BuyBinanceSellBybit,
            100m,
            0.999m,
            1.000m,
            1.004m,
            1.005m,
            netEdgeUsd + 0.10m,
            10m,
            0.10m,
            0.10m,
            0.20m,
            0.01m,
            netEdgeUsd,
            5m,
            netEdgeUsd,
            netEdgeUsd,
            signalClass,
            DataHealthFlags.None);
}
