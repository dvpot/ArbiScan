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
        IReadOnlyCollection<HealthEvent> healthEvents)
    {
        var orderedHealth = healthEvents.OrderBy(x => x.TimestampUtc).ToArray();
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

        var topDegradationCauses = orderedHealth
            .Where(x => !x.IsHealthyAfterEvent && x.Flags != DataHealthFlags.None)
            .SelectMany(x => ExpandFlags(x.Flags))
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var longestStaleIntervalMsByExchange = Exchanges.ToDictionary(
            x => x.ToString(),
            x => CalculateLongestStaleIntervalMs(orderedHealth, fromUtc, toUtc, x));

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
            topDegradationCauses,
            longestStaleIntervalMsByExchange,
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

    private static long CalculateLongestStaleIntervalMs(
        IReadOnlyList<HealthEvent> healthEvents,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        ExchangeId exchange)
    {
        DateTimeOffset? startedAtUtc = null;
        var longestMs = 0L;

        foreach (var healthEvent in healthEvents)
        {
            if (healthEvent.TimestampUtc < fromUtc || healthEvent.TimestampUtc > toUtc)
            {
                continue;
            }

            if (healthEvent.EventType == HealthEventType.StaleQuotesDetected && HasStaleFlag(healthEvent.Flags, exchange))
            {
                startedAtUtc ??= healthEvent.TimestampUtc;
                continue;
            }

            if (startedAtUtc.HasValue &&
                (healthEvent.EventType == HealthEventType.StaleQuotesRecovered ||
                 (healthEvent.EventType == HealthEventType.OverallHealthChanged && !HasStaleFlag(healthEvent.Flags, exchange))))
            {
                longestMs = Math.Max(longestMs, (long)(healthEvent.TimestampUtc - startedAtUtc.Value).TotalMilliseconds);
                startedAtUtc = null;
            }
        }

        if (startedAtUtc.HasValue)
        {
            longestMs = Math.Max(longestMs, (long)(toUtc - startedAtUtc.Value).TotalMilliseconds);
        }

        return longestMs;
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
}
