using System.Text.Json;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Infrastructure.Reporting;

public sealed class JsonReportExporter : IReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _reportsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonReportExporter(string reportsPath)
    {
        _reportsPath = reportsPath;
        Directory.CreateDirectory(_reportsPath);
    }

    public Task ExportOrderBookSnapshotAsync(OrderBookSnapshotRecord snapshot, CancellationToken cancellationToken) =>
        AppendJsonLineAsync(Path.Combine(_reportsPath, $"orderbook-snapshots-{snapshot.TimestampUtc:yyyyMMdd}.jsonl"), snapshot, cancellationToken);

    public Task ExportHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken) =>
        AppendJsonLineAsync(Path.Combine(_reportsPath, $"health-events-{healthEvent.TimestampUtc:yyyyMMdd}.jsonl"), healthEvent, cancellationToken);

    public async Task ExportHealthReportAsync(HealthReport report, CancellationToken cancellationToken)
    {
        var periodFile = Path.Combine(_reportsPath, $"health-{report.Period.ToString().ToLowerInvariant()}-{report.GeneratedAtUtc:yyyyMMddHHmmss}.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(periodFile, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ExportWindowEventAsync(OpportunityWindowEvent windowEvent, CancellationToken cancellationToken) =>
        AppendJsonLineAsync(Path.Combine(_reportsPath, $"window-events-{windowEvent.OpenedAtUtc:yyyyMMdd}.jsonl"), windowEvent, cancellationToken);

    public async Task ExportSummaryAsync(SummaryReport summary, CancellationToken cancellationToken)
    {
        var periodFile = Path.Combine(_reportsPath, $"{summary.Period.ToString().ToLowerInvariant()}-{summary.GeneratedAtUtc:yyyyMMddHHmmss}.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(periodFile, JsonSerializer.Serialize(summary, JsonOptions), cancellationToken);
            await AppendJsonLineAsync(Path.Combine(_reportsPath, $"summaries-{summary.GeneratedAtUtc:yyyyMMdd}.jsonl"), summary, cancellationToken, holdGate: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AppendJsonLineAsync(string path, object payload, CancellationToken cancellationToken, bool holdGate = false)
    {
        if (!holdGate)
        {
            await _gate.WaitAsync(cancellationToken);
        }

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            if (!holdGate)
            {
                _gate.Release();
            }
        }
    }
}
