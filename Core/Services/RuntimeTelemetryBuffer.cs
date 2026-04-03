using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class RuntimeTelemetryBuffer
{
    private readonly List<EvaluationTelemetrySnapshot> _evaluationTelemetry = [];
    private readonly List<RejectedPositiveSignalEvent> _rejectedPositiveSignals = [];
    private readonly List<StaleDiagnosticEvent> _staleDiagnostics = [];

    public void RecordEvaluationTelemetry(EvaluationTelemetrySnapshot snapshot) =>
        _evaluationTelemetry.Add(snapshot);

    public void RecordRejectedPositiveSignal(RejectedPositiveSignalEvent signalEvent) =>
        _rejectedPositiveSignals.Add(signalEvent);

    public void RecordStaleDiagnostic(StaleDiagnosticEvent diagnosticEvent) =>
        _staleDiagnostics.Add(diagnosticEvent);

    public IReadOnlyCollection<EvaluationTelemetrySnapshot> GetEvaluationTelemetry(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _evaluationTelemetry
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc)
            .ToArray();

    public IReadOnlyCollection<RejectedPositiveSignalEvent> GetRejectedPositiveSignals(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _rejectedPositiveSignals
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc)
            .ToArray();

    public IReadOnlyCollection<StaleDiagnosticEvent> GetStaleDiagnostics(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _staleDiagnostics
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc)
            .ToArray();
}
