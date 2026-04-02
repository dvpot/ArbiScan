namespace ArbiScan.Core.Models;

public sealed record EvaluationTelemetrySnapshot(
    DateTimeOffset TimestampUtc,
    SummaryDebugStats DebugStats);
