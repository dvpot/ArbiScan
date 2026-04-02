using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class RuntimeTelemetryBuffer
{
    private readonly List<EvaluationTelemetrySnapshot> _evaluationTelemetry = [];
    private readonly List<StaleDiagnosticEvent> _staleDiagnostics = [];

    public void RecordEvaluationTelemetry(EvaluationTelemetrySnapshot snapshot) =>
        _evaluationTelemetry.Add(snapshot);

    public void RecordStaleDiagnostic(StaleDiagnosticEvent diagnosticEvent) =>
        _staleDiagnostics.Add(diagnosticEvent);

    public IReadOnlyCollection<EvaluationTelemetrySnapshot> GetEvaluationTelemetry(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _evaluationTelemetry
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc)
            .ToArray();

    public IReadOnlyCollection<StaleDiagnosticEvent> GetStaleDiagnostics(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _staleDiagnostics
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc)
            .ToArray();
}
