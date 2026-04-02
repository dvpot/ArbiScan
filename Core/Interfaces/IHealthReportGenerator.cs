using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IHealthReportGenerator
{
    HealthReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        IReadOnlyCollection<HealthEvent> healthEvents);
}
