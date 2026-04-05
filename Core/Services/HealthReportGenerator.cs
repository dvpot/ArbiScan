using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class HealthReportGenerator : IHealthReportGenerator
{
    public HealthReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        DateTimeOffset startedAtUtc,
        IReadOnlyCollection<HealthEvent> healthEvents)
    {
        var reconnectCount = healthEvents.Count(x => x.EventType == HealthEventType.ExchangeRecovered);
        var staleCount = healthEvents.Count(x => x.EventType == HealthEventType.StaleQuotesDetected);
        var marketDataErrorCount = healthEvents.Count(x => x.EventType == HealthEventType.ExchangeError);
        var unhealthyDurations = BuildUnhealthyDurations(fromUtc, toUtc, healthEvents);
        var maxStaleDurationMs = BuildMaxStaleDurationMs(fromUtc, toUtc, healthEvents);
        var totalDegradedDurationMs = unhealthyDurations.Values.Sum();

        return new HealthReport(
            period,
            DateTimeOffset.UtcNow,
            fromUtc,
            toUtc,
            symbol,
            Math.Max(0, (long)(toUtc - startedAtUtc).TotalMilliseconds),
            reconnectCount,
            staleCount,
            maxStaleDurationMs,
            totalDegradedDurationMs,
            marketDataErrorCount,
            unhealthyDurations,
            $"Переподключений={reconnectCount}, stale-интервалов={staleCount}, ошибок market data={marketDataErrorCount}, суммарная деградация={totalDegradedDurationMs} ms.");
    }

    private static IReadOnlyDictionary<string, long> BuildUnhealthyDurations(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<HealthEvent> events)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [ExchangeId.Binance.ToString()] = 0L,
            [ExchangeId.Bybit.ToString()] = 0L
        };

        foreach (var exchange in Enum.GetValues<ExchangeId>())
        {
            var exchangeEvents = events
                .Where(x => x.Exchange == exchange)
                .OrderBy(x => x.TimestampUtc)
                .ToArray();
            var unhealthySince = (DateTimeOffset?)null;

            foreach (var healthEvent in exchangeEvents)
            {
                if (healthEvent.TimestampUtc < fromUtc || healthEvent.TimestampUtc > toUtc)
                {
                    continue;
                }

                if (!healthEvent.IsHealthyAfterEvent && unhealthySince is null)
                {
                    unhealthySince = healthEvent.TimestampUtc;
                }
                else if (healthEvent.IsHealthyAfterEvent && unhealthySince.HasValue)
                {
                    totals[exchange.ToString()] += Math.Max(0, (long)(healthEvent.TimestampUtc - unhealthySince.Value).TotalMilliseconds);
                    unhealthySince = null;
                }
            }

            if (unhealthySince.HasValue)
            {
                totals[exchange.ToString()] += Math.Max(0, (long)(toUtc - unhealthySince.Value).TotalMilliseconds);
            }
        }

        return totals;
    }

    private static long BuildMaxStaleDurationMs(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<HealthEvent> events)
    {
        long max = 0;
        foreach (var exchange in Enum.GetValues<ExchangeId>())
        {
            var staleSince = (DateTimeOffset?)null;
            foreach (var healthEvent in events.Where(x => x.Exchange == exchange).OrderBy(x => x.TimestampUtc))
            {
                if (healthEvent.TimestampUtc < fromUtc || healthEvent.TimestampUtc > toUtc)
                {
                    continue;
                }

                if (healthEvent.EventType == HealthEventType.StaleQuotesDetected && staleSince is null)
                {
                    staleSince = healthEvent.TimestampUtc;
                }
                else if (healthEvent.EventType == HealthEventType.StaleQuotesRecovered && staleSince.HasValue)
                {
                    max = Math.Max(max, Math.Max(0, (long)(healthEvent.TimestampUtc - staleSince.Value).TotalMilliseconds));
                    staleSince = null;
                }
            }

            if (staleSince.HasValue)
            {
                max = Math.Max(max, Math.Max(0, (long)(toUtc - staleSince.Value).TotalMilliseconds));
            }
        }

        return max;
    }
}
