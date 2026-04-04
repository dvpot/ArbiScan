using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface ISummaryGenerator
{
    SummaryReport Generate(
        SummaryPeriod period,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string symbol,
        IReadOnlyCollection<RawSignalEvent> signalEvents,
        IReadOnlyCollection<OpportunityWindowEvent> windows);
}
