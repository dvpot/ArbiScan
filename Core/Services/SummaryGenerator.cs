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
        IReadOnlyCollection<OpportunityWindowEvent> windows,
        IReadOnlyCollection<HealthEvent> healthEvents,
        IReadOnlyCollection<EvaluationTelemetrySnapshot> evaluationTelemetry)
    {
        var orderedWindows = windows.OrderBy(x => x.OpenedAtUtc).ToArray();
        var orderedHealth = healthEvents.OrderBy(x => x.TimestampUtc).ToArray();
        var debugStats = AggregateDebugStats(evaluationTelemetry);
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var fillabilityDiagnostics = BuildFillabilityDiagnostics(period, generatedAtUtc, fromUtc, toUtc, symbol, evaluationTelemetry);
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
            fillabilityDiagnostics,
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
        var rawPositive = evaluationTelemetry.Where(x => x.IsRawPositiveCross).ToArray();
        var rejected = rawPositive.Where(x => !x.IsProfitable).ToArray();

        var rawPositiveByDirection = rawPositive
            .GroupBy(x => x.Direction.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var rejectReasonCountsByDirection = rejected
            .SelectMany(x => x.RejectReasons.Select(reason => $"{x.Direction}:{reason}"))
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        var rejectReasonCountsByNotional = rejected
            .SelectMany(x => x.RejectReasons.Select(reason => $"{x.TestNotionalUsd:0.########}:{reason}"))
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        var rejectReasonCountsByDirectionAndNotional = rejected
            .SelectMany(x => x.RejectReasons.Select(reason => $"{x.Direction}:{x.TestNotionalUsd:0.########}:{reason}"))
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        var primaryRejectReasonCounts = rejected
            .Where(x => !string.IsNullOrWhiteSpace(x.PrimaryRejectReason))
            .GroupBy(x => x.PrimaryRejectReason!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var secondaryRejectReasonCounts = rejected
            .SelectMany(x => x.SecondaryRejectReasons)
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var rejectedOnlyDueToFeesCount = rejected.Count(x => x.RejectReasons.SequenceEqual(["fees"]));
        var rejectedOnlyDueToFillabilityCount = rejected.Count(x => x.RejectReasons.SequenceEqual(["fillability"]));
        var rejectedDueToFeesAndFillabilityCount = rejected.Count(x => x.RejectReasons.Contains("fees") && x.RejectReasons.Contains("fillability"));
        var rejectedDueToMultipleReasonsCount = rejected.Count(x => x.RejectReasons.Count > 1);

        return new SummaryDebugStats(
            rawPositive.Length,
            rejected.Count(x => x.RejectReasons.Contains("fees")),
            rejected.Count(x => x.RejectReasons.Contains("buffers")),
            rejected.Count(x => x.RejectReasons.Contains("health")),
            rejected.Count(x => x.RejectReasons.Contains("min_lifetime")),
            rejected.Count(x => x.RejectReasons.Contains("fillability")),
            rejected.Count(x => x.RejectReasons.Contains("rules")),
            rejected.Count(x => x.RejectReasons.Contains("other")),
            rejectedOnlyDueToFeesCount,
            rejectedOnlyDueToFillabilityCount,
            rejectedDueToFeesAndFillabilityCount,
            rejectedDueToMultipleReasonsCount,
            rejected.Count(x => x.WouldBeProfitableWithoutFees),
            rejected.Count(x => x.WouldBeProfitableWithoutFillability),
            rawPositiveByDirection,
            primaryRejectReasonCounts,
            secondaryRejectReasonCounts,
            rejectReasonCountsByDirection,
            rejectReasonCountsByNotional,
            rejectReasonCountsByDirectionAndNotional);
    }

    private static FillabilityDiagnosticsReport BuildFillabilityDiagnostics(
        SummaryPeriod period,
        DateTimeOffset generatedAtUtc,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        IReadOnlyCollection<EvaluationTelemetrySnapshot> evaluationTelemetry)
    {
        var rawPositive = evaluationTelemetry
            .Where(x => x.IsRawPositiveCross)
            .OrderBy(x => x.TimestampUtc)
            .ToArray();

        var fillableCountByNotional = GroupByNotional(rawPositive, x => x.FillabilityStatus == FillabilityStatus.Fillable);
        var partiallyFillableCountByNotional = GroupByNotional(rawPositive, x => x.FillabilityStatus == FillabilityStatus.PartiallyFillable);
        var notFillableCountByNotional = GroupByNotional(rawPositive, x => x.FillabilityStatus == FillabilityStatus.NotFillable);
        var averageRequiredQuantityByNotional = AverageByNotional(rawPositive, x => x.FillabilityDecision.RequiredBaseQuantity);
        var averageAvailableTop1QuantityByNotional = AverageByNotional(rawPositive, x => x.FillabilityDecision.EffectiveTopOfBookQuantity);
        var averageAvailableTopNQuantityByNotional = AverageByNotional(rawPositive, x => x.FillabilityDecision.EffectiveAggregatedFillableQuantity);
        var medianAvailableTopNQuantityByNotional = MedianByNotional(rawPositive, x => x.FillabilityDecision.EffectiveAggregatedFillableQuantity);
        var averageRoundedExecutableQuantityByNotional = AverageByNotional(rawPositive, x => x.FillabilityDecision.EffectiveExecutableQuantity);

        var topReasonsOfNonFillability = rawPositive
            .Where(x => x.RejectReasons.Contains("fillability") || x.FillabilityStatus != FillabilityStatus.Fillable)
            .GroupBy(x => x.FillabilityDecision.DecisionCode, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var topReasonsOfNonFillabilityByNotional = rawPositive
            .Where(x => x.RejectReasons.Contains("fillability") || x.FillabilityStatus != FillabilityStatus.Fillable)
            .GroupBy(x => $"{x.TestNotionalUsd:0.########}:{x.FillabilityDecision.DecisionCode}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new FillabilityDiagnosticsReport(
            period,
            generatedAtUtc,
            fromUtc,
            toUtc,
            symbol,
            rawPositive.Length,
            fillableCountByNotional,
            partiallyFillableCountByNotional,
            notFillableCountByNotional,
            averageRequiredQuantityByNotional,
            averageAvailableTop1QuantityByNotional,
            averageAvailableTopNQuantityByNotional,
            medianAvailableTopNQuantityByNotional,
            averageRoundedExecutableQuantityByNotional,
            topReasonsOfNonFillability,
            topReasonsOfNonFillabilityByNotional);
    }

    private static IReadOnlyDictionary<string, int> GroupByNotional(
        IEnumerable<EvaluationTelemetrySnapshot> telemetry,
        Func<EvaluationTelemetrySnapshot, bool> predicate) =>
        telemetry
            .Where(predicate)
            .GroupBy(x => x.TestNotionalUsd.ToString("0.########"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, decimal> AverageByNotional(
        IEnumerable<EvaluationTelemetrySnapshot> telemetry,
        Func<EvaluationTelemetrySnapshot, decimal> selector) =>
        telemetry
            .GroupBy(x => x.TestNotionalUsd.ToString("0.########"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any() ? g.Average(selector) : 0m, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, decimal> MedianByNotional(
        IEnumerable<EvaluationTelemetrySnapshot> telemetry,
        Func<EvaluationTelemetrySnapshot, decimal> selector) =>
        telemetry
            .GroupBy(x => x.TestNotionalUsd.ToString("0.########"), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var values = g.Select(selector).OrderBy(x => x).ToArray();
                    return Median(values);
                },
                StringComparer.Ordinal);

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
                   $"fees={debugStats.RejectedDueToFeesCount}, buffers={debugStats.RejectedDueToBuffersCount}, minLifetime={debugStats.RejectedDueToMinLifetimeCount}, rules={debugStats.RejectedDueToRulesCount}. " +
                   $"Primary reject counts: {FormatTopReasons(debugStats.PrimaryRejectReasonCounts)}. " +
                   $"What-if: without fees={debugStats.WouldBeProfitableWithoutFeesCount}, without fillability={debugStats.WouldBeProfitableWithoutFillabilityCount}.";
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

    private static string FormatTopReasons(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value}"));
}
