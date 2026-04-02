using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class HealthReportGenerator : IHealthReportGenerator
{
    private static readonly ExchangeId[] Exchanges = [ExchangeId.Binance, ExchangeId.Bybit];

    public HealthReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        IReadOnlyCollection<HealthEvent> healthEvents,
        IReadOnlyCollection<StaleDiagnosticEvent> staleDiagnostics)
    {
        var orderedHealth = healthEvents.OrderBy(x => x.TimestampUtc).ToArray();
        var diagnosticsByExchange = staleDiagnostics
            .OrderBy(x => x.TimestampUtc)
            .GroupBy(x => x.Exchange)
            .ToDictionary(x => x.Key, x => x.ToArray());
        var healthyDurationMs = CalculateDuration(orderedHealth, fromUtc, toUtc, healthy: true);
        var degradedDurationMs = Math.Max(0, (long)(toUtc - fromUtc).TotalMilliseconds - healthyDurationMs);

        var reconnectCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth.Count(e => e.Exchange == x && e.EventType == HealthEventType.ReconnectStarted));

        var resyncCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth.Count(e => e.Exchange == x && e.EventType == HealthEventType.ResyncStarted));

        var staleCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth.Count(e => e.EventType == HealthEventType.StaleQuotesDetected && HasStaleFlag(e.Flags, x)));

        var staleDetectedCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => GetDiagnosticsForExchange(diagnosticsByExchange, x).Count(e => e.EventType == "stale_detected"));

        var staleRecoveredCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => GetDiagnosticsForExchange(diagnosticsByExchange, x).Count(e => e.EventType == "stale_recovered"));

        var staleIntervalsByExchange = Exchanges.ToDictionary(
            x => x,
            x => BuildStaleIntervals(GetDiagnosticsForExchange(diagnosticsByExchange, x)));

        var staleFlapCountByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => staleIntervalsByExchange[x].Count(interval => interval <= Math.Max(
                GetDiagnosticsForExchange(diagnosticsByExchange, x).FirstOrDefault()?.QuoteStalenessConfirmationMs * 2d ?? 0d,
                GetDiagnosticsForExchange(diagnosticsByExchange, x).FirstOrDefault()?.ScanIntervalMs * 3d ?? 0d)));

        var topDegradationCauses = orderedHealth
            .Where(x => !x.IsHealthyAfterEvent && x.Flags != DataHealthFlags.None)
            .SelectMany(x => ExpandFlags(x.Flags))
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var longestStaleIntervalMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => staleIntervalsByExchange[x].Count == 0 ? 0L : (long)staleIntervalsByExchange[x].Max());

        var averageStaleDurationMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Average(staleIntervalsByExchange[x]));

        var medianStaleDurationMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Percentile(staleIntervalsByExchange[x], 50));

        var maxStaleDurationMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => staleIntervalsByExchange[x].Count == 0 ? 0d : staleIntervalsByExchange[x].Max());

        var p95StaleDurationMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Percentile(staleIntervalsByExchange[x], 95));

        var averageDataAgeMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Average(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.DataAgeMs)));

        var p95DataAgeMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Percentile(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.DataAgeMs), 95));

        var maxDataAgeMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => MaxOrDefault(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.DataAgeMs)));

        var averageCallbackSilenceMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Average(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.CallbackSilenceMs)));

        var p95CallbackSilenceMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => Percentile(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.CallbackSilenceMs), 95));

        var maxCallbackSilenceMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => MaxOrDefault(GetDiagnosticsForExchange(diagnosticsByExchange, x).Select(e => e.CallbackSilenceMs)));

        var staleLikelyRootCauseByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => DetermineDominantRootCause(GetDiagnosticsForExchange(diagnosticsByExchange, x)));

        var lastStaleDetectedAtUtcByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth
                .Where(e => e.EventType == HealthEventType.StaleQuotesDetected && HasStaleFlag(e.Flags, x))
                .Select(e => (DateTimeOffset?)e.TimestampUtc)
                .LastOrDefault());

        var lastReconnectAtUtcByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth
                .Where(e => e.Exchange == x && e.EventType == HealthEventType.ReconnectStarted)
                .Select(e => (DateTimeOffset?)e.TimestampUtc)
                .LastOrDefault());

        var lastResyncAtUtcByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => orderedHealth
                .Where(e => e.Exchange == x && e.EventType == HealthEventType.ResyncStarted)
                .Select(e => (DateTimeOffset?)e.TimestampUtc)
                .LastOrDefault());

        return new HealthReport(
            period,
            DateTimeOffset.UtcNow,
            fromUtc,
            toUtc,
            symbol,
            healthyDurationMs,
            degradedDurationMs,
            reconnectCountByExchange,
            resyncCountByExchange,
            staleCountByExchange,
            staleDetectedCountByExchange,
            staleRecoveredCountByExchange,
            staleFlapCountByExchange,
            topDegradationCauses,
            longestStaleIntervalMsByExchange,
            averageStaleDurationMsByExchange,
            medianStaleDurationMsByExchange,
            maxStaleDurationMsByExchange,
            p95StaleDurationMsByExchange,
            averageDataAgeMsByExchange,
            p95DataAgeMsByExchange,
            maxDataAgeMsByExchange,
            averageCallbackSilenceMsByExchange,
            p95CallbackSilenceMsByExchange,
            maxCallbackSilenceMsByExchange,
            staleLikelyRootCauseByExchange,
            lastStaleDetectedAtUtcByExchange,
            lastReconnectAtUtcByExchange,
            lastResyncAtUtcByExchange);
    }

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

    private static IReadOnlyList<double> BuildStaleIntervals(IReadOnlyList<StaleDiagnosticEvent> diagnostics)
    {
        DateTimeOffset? startedAtUtc = null;
        var intervals = new List<double>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.EventType == "stale_detected")
            {
                startedAtUtc ??= diagnostic.TimestampUtc;
                continue;
            }

            if (startedAtUtc.HasValue && diagnostic.EventType == "stale_recovered")
            {
                intervals.Add((diagnostic.TimestampUtc - startedAtUtc.Value).TotalMilliseconds);
                startedAtUtc = null;
            }
        }

        if (startedAtUtc.HasValue)
        {
            intervals.Add((diagnostics.Last().TimestampUtc - startedAtUtc.Value).TotalMilliseconds);
        }

        return intervals;
    }

    private static bool HasStaleFlag(DataHealthFlags flags, ExchangeId exchange) =>
        exchange switch
        {
            ExchangeId.Binance => flags.HasFlag(DataHealthFlags.BinanceStale),
            ExchangeId.Bybit => flags.HasFlag(DataHealthFlags.BybitStale),
            _ => false
        };

    private static IEnumerable<string> ExpandFlags(DataHealthFlags flags)
    {
        foreach (var value in Enum.GetValues<DataHealthFlags>())
        {
            if (value == DataHealthFlags.None || value == DataHealthFlags.Degraded)
            {
                continue;
            }

            if (flags.HasFlag(value))
            {
                yield return value.ToString();
            }
        }
    }

    private static IReadOnlyList<StaleDiagnosticEvent> GetDiagnosticsForExchange(
        IReadOnlyDictionary<ExchangeId, StaleDiagnosticEvent[]> diagnosticsByExchange,
        ExchangeId exchange) =>
        diagnosticsByExchange.TryGetValue(exchange, out var diagnostics)
            ? diagnostics
            : [];

    private static double Average(IEnumerable<double> values)
    {
        var array = values as double[] ?? values.ToArray();
        return array.Length == 0 ? 0d : array.Average();
    }

    private static double MaxOrDefault(IEnumerable<double> values)
    {
        var array = values as double[] ?? values.ToArray();
        return array.Length == 0 ? 0d : array.Max();
    }

    private static double Percentile(IEnumerable<double> values, int percentile)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0)
        {
            return 0d;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var index = (percentile / 100d) * (ordered.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return ordered[lower];
        }

        var fraction = index - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static string DetermineDominantRootCause(IReadOnlyList<StaleDiagnosticEvent> diagnostics)
    {
        var dominant = diagnostics
            .Where(x => x.EventType == "stale_detected")
            .GroupBy(x => x.StaleLikelyRootCause)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(dominant) ? "unknown" : dominant;
    }
}
