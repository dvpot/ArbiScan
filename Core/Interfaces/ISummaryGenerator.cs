using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface ISummaryGenerator
{
    SummaryReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<OpportunityWindowEvent> windows,
        IReadOnlyCollection<HealthEvent> healthEvents);
}
