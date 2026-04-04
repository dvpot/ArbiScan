using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IOpportunityRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveRawSignalEventAsync(RawSignalEvent signalEvent, CancellationToken cancellationToken);
    Task SaveHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken);
    Task SaveWindowEventAsync(OpportunityWindowEvent windowEvent, CancellationToken cancellationToken);
    Task SaveSummaryAsync(SummaryReport summary, CancellationToken cancellationToken);
    Task SaveHealthReportAsync(HealthReport report, CancellationToken cancellationToken);
    Task<IReadOnlyList<RawSignalEvent>> GetRawSignalEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpportunityWindowEvent>> GetWindowEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<HealthEvent>> GetHealthEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}
