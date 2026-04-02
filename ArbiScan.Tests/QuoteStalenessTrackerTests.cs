using ArbiScan.Core.Enums;
using ArbiScan.Core.Services;

namespace ArbiScan.Tests;

public sealed class QuoteStalenessTrackerTests
{
    [Fact]
    public void IsStale_WaitsForContinuousConfirmationWindow()
    {
        var tracker = new QuoteStalenessTracker();
        var threshold = TimeSpan.FromSeconds(3);
        var confirmationWindow = TimeSpan.FromSeconds(3);
        var startedAtUtc = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc, TimeSpan.FromSeconds(2), threshold, confirmationWindow));
        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(4), threshold, confirmationWindow));
        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(5.5), TimeSpan.FromSeconds(5.5), threshold, confirmationWindow));
        Assert.True(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(6.25), TimeSpan.FromSeconds(6.25), threshold, confirmationWindow));
    }

    [Fact]
    public void IsStale_ResetsAfterFreshUpdate()
    {
        var tracker = new QuoteStalenessTracker();
        var threshold = TimeSpan.FromSeconds(3);
        var confirmationWindow = TimeSpan.FromSeconds(2);
        var startedAtUtc = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(6), TimeSpan.FromSeconds(6), threshold, confirmationWindow));
        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(6.1), TimeSpan.FromMilliseconds(250), threshold, confirmationWindow));
        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(10.4), TimeSpan.FromSeconds(4.3), threshold, confirmationWindow));
    }

    [Fact]
    public void Reset_ClearsExchangeState()
    {
        var tracker = new QuoteStalenessTracker();
        var threshold = TimeSpan.FromSeconds(3);
        var confirmationWindow = TimeSpan.FromSeconds(2);
        var startedAtUtc = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(4), threshold, confirmationWindow));

        tracker.Reset(ExchangeId.Bybit);

        Assert.False(tracker.IsStale(ExchangeId.Bybit, startedAtUtc.AddSeconds(4.5), TimeSpan.FromSeconds(4.5), threshold, confirmationWindow));
    }
}
