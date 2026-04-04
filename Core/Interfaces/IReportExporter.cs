using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IReportExporter
{
    Task ExportRawSignalEventAsync(RawSignalEvent signalEvent, CancellationToken cancellationToken);
    Task ExportHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken);
    Task ExportHealthReportAsync(HealthReport report, CancellationToken cancellationToken);
    Task ExportWindowEventAsync(OpportunityWindowEvent windowEvent, CancellationToken cancellationToken);
    Task ExportSummaryAsync(SummaryReport summary, CancellationToken cancellationToken);
}
