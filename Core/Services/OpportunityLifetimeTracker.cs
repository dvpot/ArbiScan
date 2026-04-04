using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class OpportunityLifetimeTracker
{
    private readonly Dictionary<string, ActiveOpportunityWindow> _activeWindows = new(StringComparer.Ordinal);

    public IReadOnlyList<OpportunityWindowEvent> Process(RawSignalEvent signalEvent)
    {
        var key = CreateKey(signalEvent.Direction, signalEvent.TestNotionalUsd);
        var isPositive = signalEvent.SignalClass != SignalClass.NonPositive;

        if (isPositive)
        {
            if (_activeWindows.TryGetValue(key, out var active))
            {
                active.MaxGrossSpreadUsd = Math.Max(active.MaxGrossSpreadUsd, signalEvent.GrossSpreadUsd);
                active.MaxNetEdgeUsd = Math.Max(active.MaxNetEdgeUsd, signalEvent.NetEdgeUsd);
                active.NetEdgeSumUsd += signalEvent.NetEdgeUsd;
                active.ObservationCount++;
                if ((int)signalEvent.SignalClass > (int)active.StrongestSignalClass)
                {
                    active.StrongestSignalClass = signalEvent.SignalClass;
                }

                return [];
            }

            _activeWindows[key] = new ActiveOpportunityWindow
            {
                WindowId = Guid.NewGuid().ToString("N"),
                Symbol = signalEvent.Symbol,
                Direction = signalEvent.Direction,
                TestNotionalUsd = signalEvent.TestNotionalUsd,
                StartedAtUtc = signalEvent.TimestampUtc,
                MaxGrossSpreadUsd = signalEvent.GrossSpreadUsd,
                MaxNetEdgeUsd = signalEvent.NetEdgeUsd,
                NetEdgeSumUsd = signalEvent.NetEdgeUsd,
                ObservationCount = 1,
                StrongestSignalClass = signalEvent.SignalClass
            };

            return [];
        }

        if (!_activeWindows.Remove(key, out var closedWindow))
        {
            return [];
        }

        return [ToWindowEvent(closedWindow, signalEvent.TimestampUtc)];
    }

    public IReadOnlyList<OpportunityWindowEvent> FlushAll(DateTimeOffset timestampUtc)
    {
        var windows = _activeWindows.Values
            .Select(x => ToWindowEvent(x, timestampUtc))
            .ToArray();

        _activeWindows.Clear();
        return windows;
    }

    private static OpportunityWindowEvent ToWindowEvent(ActiveOpportunityWindow active, DateTimeOffset closedAtUtc) =>
        new(
            active.WindowId,
            active.Symbol,
            active.Direction,
            active.TestNotionalUsd,
            active.StartedAtUtc,
            closedAtUtc,
            active.LifetimeMs(closedAtUtc),
            active.MaxGrossSpreadUsd,
            active.MaxNetEdgeUsd,
            active.ObservationCount == 0 ? 0m : active.NetEdgeSumUsd / active.ObservationCount,
            active.ObservationCount,
            active.StrongestSignalClass);

    private static string CreateKey(ArbitrageDirection direction, decimal notionalUsd) =>
        $"{direction}:{notionalUsd:0.########}";
}
