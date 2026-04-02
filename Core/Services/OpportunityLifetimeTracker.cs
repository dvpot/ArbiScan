using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class OpportunityLifetimeTracker
{
    private readonly Dictionary<string, ActiveOpportunityWindow> _activeWindows = new(StringComparer.Ordinal);

    public OpportunityLifetimeTrackingResult Process(
        string symbol,
        OpportunityPairEvaluation evaluation,
        AppSettings settings)
    {
        var key = CreateKey(evaluation.Direction, evaluation.TestNotionalUsd);
        var profitable = evaluation.Conservative.IsProfitable(
            settings.Thresholds.EntryThresholdUsd,
            settings.Thresholds.EntryThresholdBps);

        if (profitable)
        {
            if (_activeWindows.TryGetValue(key, out var existing))
            {
                existing.LatestEvaluation = evaluation;
                if (evaluation.Conservative.NetPnlUsd > existing.PeakEvaluation.Conservative.NetPnlUsd)
                {
                    existing.PeakEvaluation = evaluation;
                }

                return new OpportunityLifetimeTrackingResult([], false);
            }

            _activeWindows[key] = new ActiveOpportunityWindow
            {
                WindowId = $"{evaluation.Direction}:{evaluation.TestNotionalUsd}:{evaluation.TimestampUtc.ToUnixTimeMilliseconds()}",
                Direction = evaluation.Direction,
                TestNotionalUsd = evaluation.TestNotionalUsd,
                StartedAtUtc = evaluation.TimestampUtc,
                OpeningEvaluation = evaluation,
                LatestEvaluation = evaluation,
                PeakEvaluation = evaluation
            };

            return new OpportunityLifetimeTrackingResult([], false);
        }

        if (!_activeWindows.Remove(key, out var closed))
        {
            return new OpportunityLifetimeTrackingResult([], false);
        }

        var lifetimeMs = closed.LifetimeMs(evaluation.TimestampUtc);
        if (lifetimeMs < settings.MinWindowLifetimeMs)
        {
            return new OpportunityLifetimeTrackingResult([], true);
        }

        var peak = closed.PeakEvaluation;
        return new OpportunityLifetimeTrackingResult(
        [
            new OpportunityWindowEvent(
                closed.WindowId,
                closed.StartedAtUtc,
                evaluation.TimestampUtc,
                lifetimeMs,
                symbol,
                peak.Direction,
                peak.Direction.BuyExchange().ToString(),
                peak.Direction.SellExchange().ToString(),
                peak.TestNotionalUsd,
                peak.Conservative.FillabilityStatus,
                peak.Conservative.ExecutableQuantity,
                peak.Conservative.FillableBaseQuantity,
                peak.BinanceBestBid,
                peak.BinanceBestAsk,
                peak.BybitBestBid,
                peak.BybitBestAsk,
                peak.Conservative.GrossPnlUsd,
                peak.Optimistic.NetPnlUsd,
                peak.Conservative.NetPnlUsd,
                peak.Conservative.GrossEdgePct,
                peak.Optimistic.NetEdgePct,
                peak.Conservative.NetEdgePct,
                peak.Conservative.FeesTotalUsd,
                peak.Conservative.BuffersTotalUsd,
                peak.HealthFlags,
                peak.Conservative.RejectReason)
        ],
        false);
    }

    public IReadOnlyList<OpportunityWindowEvent> Flush(string symbol, DateTimeOffset closedAtUtc, AppSettings settings)
    {
        var events = new List<OpportunityWindowEvent>();

        foreach (var active in _activeWindows.Values)
        {
            var lifetimeMs = active.LifetimeMs(closedAtUtc);
            if (lifetimeMs < settings.MinWindowLifetimeMs)
            {
                continue;
            }

            var peak = active.PeakEvaluation;
            events.Add(new OpportunityWindowEvent(
                active.WindowId,
                active.StartedAtUtc,
                closedAtUtc,
                lifetimeMs,
                symbol,
                peak.Direction,
                peak.Direction.BuyExchange().ToString(),
                peak.Direction.SellExchange().ToString(),
                peak.TestNotionalUsd,
                peak.Conservative.FillabilityStatus,
                peak.Conservative.ExecutableQuantity,
                peak.Conservative.FillableBaseQuantity,
                peak.BinanceBestBid,
                peak.BinanceBestAsk,
                peak.BybitBestBid,
                peak.BybitBestAsk,
                peak.Conservative.GrossPnlUsd,
                peak.Optimistic.NetPnlUsd,
                peak.Conservative.NetPnlUsd,
                peak.Conservative.GrossEdgePct,
                peak.Optimistic.NetEdgePct,
                peak.Conservative.NetEdgePct,
                peak.Conservative.FeesTotalUsd,
                peak.Conservative.BuffersTotalUsd,
                peak.HealthFlags,
                "Window flushed during shutdown"));
        }

        _activeWindows.Clear();
        return events;
    }

    private static string CreateKey(ArbitrageDirection direction, decimal notionalUsd) =>
        $"{direction}:{notionalUsd}";
}
