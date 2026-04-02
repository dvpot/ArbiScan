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
        IReadOnlyCollection<OpportunityWindowEvent> windows,
        IReadOnlyCollection<HealthEvent> healthEvents,
        IReadOnlyCollection<EvaluationTelemetrySnapshot> evaluationTelemetry)
    {
        var orderedWindows = windows.OrderBy(x => x.OpenedAtUtc).ToArray();
        var orderedHealth = healthEvents.OrderBy(x => x.TimestampUtc).ToArray();
        var debugStats = AggregateDebugStats(evaluationTelemetry);
        var symbol = orderedWindows.FirstOrDefault()?.Symbol ?? "N/A";
        var lifetimes = orderedWindows.Select(x => (double)x.LifetimeMs).OrderBy(x => x).ToArray();
        var netPnls = orderedWindows.Select(x => x.ConservativeNetPnlUsd).OrderBy(x => x).ToArray();
        var fillableSizes = orderedWindows.Select(x => x.FillableBaseQuantity).OrderBy(x => x).ToArray();

        var healthyMs = CalculateDuration(orderedHealth, fromUtc, toUtc, healthy: true);
        var degradedMs = Math.Max(0, (long)(toUtc - fromUtc).TotalMilliseconds - healthyMs);

        return new SummaryReport(
            period,
            DateTimeOffset.UtcNow,
            fromUtc,
            toUtc,
            symbol,
            orderedWindows.Length,
            orderedWindows.GroupBy(x => x.Direction.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            orderedWindows.GroupBy(x => x.TestNotionalUsd.ToString("0.########")).ToDictionary(g => g.Key, g => g.Count()),
            orderedWindows.Count(x => x.GrossPnlUsd > 0m),
            orderedWindows.Count(x => x.ConservativeNetPnlUsd > 0m),
            orderedWindows.Count(x => x.ConservativeNetPnlUsd > 0m),
            lifetimes.Length == 0 ? 0d : lifetimes.Average(),
            Median(lifetimes),
            orderedWindows.Length == 0 ? 0L : orderedWindows.Max(x => x.LifetimeMs),
            BuildLifetimeDistribution(orderedWindows),
            orderedWindows.Sum(x => x.GrossPnlUsd),
            orderedWindows.Sum(x => x.ConservativeNetPnlUsd),
            orderedWindows.Length == 0 ? 0m : orderedWindows.Average(x => x.ConservativeNetPnlUsd),
            Median(netPnls),
            orderedWindows.Length == 0 ? 0m : orderedWindows.Max(x => x.ConservativeNetPnlUsd),
            orderedWindows.Length == 0 ? 0m : orderedWindows.Min(x => x.ConservativeNetPnlUsd),
            BuildNetPnlDistribution(orderedWindows),
            orderedWindows.Sum(x => x.FeesTotalUsd),
            orderedWindows.Sum(x => x.BuffersTotalUsd),
            orderedWindows.Length == 0 ? 0m : orderedWindows.Average(x => x.ConservativeNetEdgePct * 100m),
            orderedWindows.Count(x => x.FillabilityStatus == FillabilityStatus.Fillable),
            orderedWindows.Count(x => x.FillabilityStatus == FillabilityStatus.PartiallyFillable),
            orderedWindows.Count(x => x.FillabilityStatus == FillabilityStatus.NotFillable),
            fillableSizes.Length == 0 ? 0m : fillableSizes.Average(),
            fillableSizes.Length == 0 ? 0m : fillableSizes.First(),
            fillableSizes.Length == 0 ? 0m : fillableSizes.Last(),
            orderedHealth.Count(x => x.EventType is HealthEventType.ReconnectStarted or HealthEventType.ReconnectCompleted),
            orderedHealth.Count(x => x.EventType is HealthEventType.ResyncStarted or HealthEventType.ResyncCompleted),
            orderedHealth.Count(x => x.EventType is HealthEventType.StaleQuotesDetected),
            healthyMs,
            degradedMs,
            debugStats,
            BuildFinalAssessment(orderedWindows, debugStats));
    }

    private static IReadOnlyDictionary<string, int> BuildLifetimeDistribution(IEnumerable<OpportunityWindowEvent> windows) =>
        windows.GroupBy(window => window.LifetimeMs switch
            {
                < 1_000 => "<1s",
                < 5_000 => "1-5s",
                < 15_000 => "5-15s",
                < 60_000 => "15-60s",
                _ => "60s+"
            })
            .ToDictionary(g => g.Key, g => g.Count());

    private static IReadOnlyDictionary<string, int> BuildNetPnlDistribution(IEnumerable<OpportunityWindowEvent> windows) =>
        windows.GroupBy(window => window.ConservativeNetPnlUsd switch
            {
                < -1m => "<-1",
                < 0m => "-1..0",
                < 1m => "0..1",
                < 5m => "1..5",
                _ => "5+"
            })
            .ToDictionary(g => g.Key, g => g.Count());

    private static long CalculateDuration(
        IReadOnlyList<HealthEvent> healthEvents,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool healthy)
    {
        var totalMs = 0L;
        var currentTimestamp = fromUtc;
        var currentHealthy = true;

        foreach (var healthEvent in healthEvents)
        {
            if (healthEvent.TimestampUtc < fromUtc || healthEvent.TimestampUtc > toUtc)
            {
                continue;
            }

            if (currentHealthy == healthy)
            {
                totalMs += (long)(healthEvent.TimestampUtc - currentTimestamp).TotalMilliseconds;
            }

            currentTimestamp = healthEvent.TimestampUtc;
            currentHealthy = healthEvent.IsHealthyAfterEvent;
        }

        if (currentHealthy == healthy)
        {
            totalMs += (long)(toUtc - currentTimestamp).TotalMilliseconds;
        }

        return totalMs;
    }

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

    private static decimal Median(decimal[] values)
    {
        if (values.Length == 0)
        {
            return 0m;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2m
            : values[middle];
    }

    private static SummaryDebugStats AggregateDebugStats(IReadOnlyCollection<EvaluationTelemetrySnapshot> evaluationTelemetry)
    {
        if (evaluationTelemetry.Count == 0)
        {
            return new SummaryDebugStats(0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new SummaryDebugStats(
            evaluationTelemetry.Sum(x => x.DebugStats.RawPositiveCrossCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToFeesCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToBuffersCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToHealthCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToMinLifetimeCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToFillabilityCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToRulesCount),
            evaluationTelemetry.Sum(x => x.DebugStats.RejectedDueToOtherCount));
    }

    private static string BuildFinalAssessment(IReadOnlyCollection<OpportunityWindowEvent> windows, SummaryDebugStats debugStats)
    {
        if (windows.Count == 0)
        {
            if (debugStats.RawPositiveCrossCount == 0)
            {
                return "Статистически значимых окон не найдено. В live-периоде не было даже raw positive cross-spread сигналов, поэтому текущие данные больше похожи на отсутствие рыночных окон, чем на баг детектора.";
            }

            return $"Статистически значимых окон не найдено. Raw positive cross-spread сигналов: {debugStats.RawPositiveCrossCount}. " +
                   $"Основные причины отсева: health={debugStats.RejectedDueToHealthCount}, fillability={debugStats.RejectedDueToFillabilityCount}, " +
                   $"fees={debugStats.RejectedDueToFeesCount}, buffers={debugStats.RejectedDueToBuffersCount}, minLifetime={debugStats.RejectedDueToMinLifetimeCount}, rules={debugStats.RejectedDueToRulesCount}.";
        }

        var bestDirection = windows
            .GroupBy(x => x.Direction)
            .OrderByDescending(g => g.Count(x => x.ConservativeNetPnlUsd > 0m))
            .First();

        var bestNotional = windows
            .GroupBy(x => x.TestNotionalUsd)
            .OrderByDescending(g => g.Count(x => x.ConservativeNetPnlUsd > 0m))
            .First();

        var positiveWindows = windows.Count(x => x.ConservativeNetPnlUsd > 0m);
        var totalNet = windows.Sum(x => x.ConservativeNetPnlUsd);
        var rejectedByVolume = windows.Count(x => x.FillabilityStatus != FillabilityStatus.Fillable);

        return $"Найдено {positiveWindows} положительных консервативных окон из {windows.Count}. " +
               $"Чаще всего положительный net edge наблюдался в направлении {bestDirection.Key} и на размере {bestNotional.Key:0.########} USD. " +
               $"Суммарный консервативный результат: {totalNet:0.########} USD. " +
               $"Основной фактор отсева: {(rejectedByVolume > 0 ? "недостаточная исполнимость и ограничения размера" : "комиссии и safety buffers")}.";
    }
}
