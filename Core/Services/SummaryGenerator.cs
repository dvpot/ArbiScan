using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class SummaryGenerator : ISummaryGenerator
{
    public SummaryReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        IReadOnlyCollection<RawSignalEvent> signalEvents,
        IReadOnlyCollection<OpportunityWindowEvent> windows)
    {
        var orderedSignals = signalEvents.OrderBy(x => x.TimestampUtc).ToArray();
        var orderedWindows = windows.OrderBy(x => x.OpenedAtUtc).ToArray();
        var lifetimes = orderedWindows.Select(x => (double)x.DurationMs).OrderBy(x => x).ToArray();

        var debugStats = new SummaryDebugStats(
            orderedSignals.Count(x => x.SignalClass == SignalClass.RawPositive),
            orderedSignals.Count(x => x.SignalClass == SignalClass.FeePositive),
            orderedSignals.Count(x => x.SignalClass == SignalClass.NetPositive),
            orderedSignals
                .GroupBy(x => $"{x.Direction}:{x.SignalClass}", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            orderedSignals
                .GroupBy(x => $"{x.TestNotionalUsd:0.########}:{x.SignalClass}", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            orderedSignals
                .GroupBy(x => BucketNetEdgeBps(x.NetEdgeBps), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));

        return new SummaryReport(
            period,
            DateTimeOffset.UtcNow,
            fromUtc,
            toUtc,
            symbol,
            orderedSignals.Length,
            orderedSignals.Count(x => x.SignalClass >= SignalClass.RawPositive),
            orderedSignals.Count(x => x.SignalClass >= SignalClass.FeePositive),
            orderedSignals.Count(x => x.SignalClass >= SignalClass.NetPositive),
            orderedSignals.Count(x => x.SignalClass == SignalClass.EntryQualified),
            orderedWindows.Length,
            orderedWindows.GroupBy(x => x.Direction.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            orderedWindows.GroupBy(x => x.TestNotionalUsd.ToString("0.########"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            lifetimes.Length == 0 ? 0d : lifetimes.Average(),
            Median(lifetimes),
            lifetimes.Length == 0 ? 0L : (long)lifetimes.Max(),
            orderedSignals.Length == 0 ? 0m : orderedSignals.Max(x => x.GrossSpreadUsd),
            orderedSignals.Length == 0 ? 0m : orderedSignals.Max(x => x.NetEdgeUsd),
            orderedSignals.Length == 0 ? 0m : orderedSignals.Average(x => x.NetEdgeUsd),
            orderedSignals.Where(x => x.SignalClass == SignalClass.EntryQualified).Sum(x => x.ExpectedNetPnlUsd),
            debugStats,
            BuildFinalAssessment(orderedSignals));
    }

    private static string BuildFinalAssessment(IReadOnlyCollection<RawSignalEvent> signals)
    {
        if (signals.Count == 0)
        {
            return "За выбранный период не было сохранено ни одного наблюдения сигнала.";
        }

        var rawPositive = signals.Count(x => x.SignalClass >= SignalClass.RawPositive);
        var feePositive = signals.Count(x => x.SignalClass >= SignalClass.FeePositive);
        var netPositive = signals.Count(x => x.SignalClass >= SignalClass.NetPositive);
        var entryQualified = signals.Count(x => x.SignalClass == SignalClass.EntryQualified);
        var bestNotional = signals
            .GroupBy(x => x.TestNotionalUsd)
            .OrderByDescending(g => g.Count(x => x.SignalClass == SignalClass.EntryQualified))
            .ThenByDescending(g => g.Count(x => x.SignalClass >= SignalClass.NetPositive))
            .First().Key;
        var bestDirection = signals
            .GroupBy(x => x.Direction)
            .OrderByDescending(g => g.Count(x => x.SignalClass == SignalClass.EntryQualified))
            .ThenByDescending(g => g.Count(x => x.SignalClass >= SignalClass.NetPositive))
            .First().Key;

        return $"Положительных raw-сигналов: {rawPositive}; пережили комиссии: {feePositive}; пережили safety buffer: {netPositive}; entry-qualified: {entryQualified}. " +
               $"Лучший notional: {bestNotional:0.########} USD. Самое частое направление: {bestDirection}. " +
               $"Типичный net edge: среднее {signals.Average(x => x.NetEdgeUsd):0.########} USD / {signals.Average(x => x.NetEdgeBps):0.########} bps. " +
               $"{(netPositive > 0 ? "Есть основания для более глубокого прикладного анализа." : "На уровне best bid / best ask рынок сейчас выглядит слабообещающим.")}";
    }

    private static string BucketNetEdgeBps(decimal value) =>
        value switch
        {
            < -10m => "<-10bps",
            < 0m => "-10..0bps",
            < 2m => "0..2bps",
            < 5m => "2..5bps",
            < 10m => "5..10bps",
            _ => "10+bps"
        };

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0d;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2d
            : values[middle];
    }
}
