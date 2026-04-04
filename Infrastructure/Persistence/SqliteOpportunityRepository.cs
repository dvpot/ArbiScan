using System.Text.Json;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using Microsoft.Data.Sqlite;

namespace ArbiScan.Infrastructure.Persistence;

public sealed class SqliteOpportunityRepository : IOpportunityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _connectionString;

    public SqliteOpportunityRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS raw_signal_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                symbol TEXT NOT NULL,
                direction INTEGER NOT NULL,
                test_notional_usd REAL NOT NULL,
                signal_class INTEGER NOT NULL,
                net_edge_usd REAL NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS health_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type INTEGER NOT NULL,
                exchange_id INTEGER NULL,
                flags INTEGER NOT NULL,
                is_healthy_after_event INTEGER NOT NULL,
                message TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS window_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                window_id TEXT NOT NULL,
                symbol TEXT NOT NULL,
                direction INTEGER NOT NULL,
                test_notional_usd REAL NOT NULL,
                opened_at_utc TEXT NOT NULL,
                closed_at_utc TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS summary_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                generated_at_utc TEXT NOT NULL,
                period INTEGER NOT NULL,
                from_utc TEXT NOT NULL,
                to_utc TEXT NOT NULL,
                symbol TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS health_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                generated_at_utc TEXT NOT NULL,
                period INTEGER NOT NULL,
                from_utc TEXT NOT NULL,
                to_utc TEXT NOT NULL,
                symbol TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveRawSignalEventAsync(RawSignalEvent signalEvent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_signal_events (
                timestamp_utc, symbol, direction, test_notional_usd, signal_class, net_edge_usd, payload_json
            ) VALUES (
                $timestamp_utc, $symbol, $direction, $test_notional_usd, $signal_class, $net_edge_usd, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$timestamp_utc", signalEvent.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$symbol", signalEvent.Symbol);
        command.Parameters.AddWithValue("$direction", (int)signalEvent.Direction);
        command.Parameters.AddWithValue("$test_notional_usd", signalEvent.TestNotionalUsd);
        command.Parameters.AddWithValue("$signal_class", (int)signalEvent.SignalClass);
        command.Parameters.AddWithValue("$net_edge_usd", signalEvent.NetEdgeUsd);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(signalEvent, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO health_events (
                timestamp_utc, event_type, exchange_id, flags, is_healthy_after_event, message
            ) VALUES (
                $timestamp_utc, $event_type, $exchange_id, $flags, $is_healthy_after_event, $message
            );
            """;

        command.Parameters.AddWithValue("$timestamp_utc", healthEvent.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$event_type", (int)healthEvent.EventType);
        command.Parameters.AddWithValue("$exchange_id", (object?)healthEvent.Exchange is null ? DBNull.Value : (int)healthEvent.Exchange.Value);
        command.Parameters.AddWithValue("$flags", (int)healthEvent.Flags);
        command.Parameters.AddWithValue("$is_healthy_after_event", healthEvent.IsHealthyAfterEvent ? 1 : 0);
        command.Parameters.AddWithValue("$message", healthEvent.Message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveWindowEventAsync(OpportunityWindowEvent windowEvent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO window_events (
                window_id, symbol, direction, test_notional_usd, opened_at_utc, closed_at_utc, duration_ms, payload_json
            ) VALUES (
                $window_id, $symbol, $direction, $test_notional_usd, $opened_at_utc, $closed_at_utc, $duration_ms, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$window_id", windowEvent.WindowId);
        command.Parameters.AddWithValue("$symbol", windowEvent.Symbol);
        command.Parameters.AddWithValue("$direction", (int)windowEvent.Direction);
        command.Parameters.AddWithValue("$test_notional_usd", windowEvent.TestNotionalUsd);
        command.Parameters.AddWithValue("$opened_at_utc", windowEvent.OpenedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$closed_at_utc", windowEvent.ClosedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$duration_ms", windowEvent.DurationMs);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(windowEvent, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSummaryAsync(SummaryReport summary, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO summary_reports (
                generated_at_utc, period, from_utc, to_utc, symbol, payload_json
            ) VALUES (
                $generated_at_utc, $period, $from_utc, $to_utc, $symbol, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$generated_at_utc", summary.GeneratedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$period", (int)summary.Period);
        command.Parameters.AddWithValue("$from_utc", summary.FromUtc.ToString("O"));
        command.Parameters.AddWithValue("$to_utc", summary.ToUtc.ToString("O"));
        command.Parameters.AddWithValue("$symbol", summary.Symbol);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(summary, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveHealthReportAsync(HealthReport report, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO health_reports (
                generated_at_utc, period, from_utc, to_utc, symbol, payload_json
            ) VALUES (
                $generated_at_utc, $period, $from_utc, $to_utc, $symbol, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$generated_at_utc", report.GeneratedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$period", (int)report.Period);
        command.Parameters.AddWithValue("$from_utc", report.FromUtc.ToString("O"));
        command.Parameters.AddWithValue("$to_utc", report.ToUtc.ToString("O"));
        command.Parameters.AddWithValue("$symbol", report.Symbol);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(report, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RawSignalEvent>> GetRawSignalEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json
            FROM raw_signal_events
            WHERE timestamp_utc >= $from_utc AND timestamp_utc <= $to_utc
            ORDER BY timestamp_utc;
            """;

        command.Parameters.AddWithValue("$from_utc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$to_utc", toUtc.ToString("O"));

        var results = new List<RawSignalEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = JsonSerializer.Deserialize<RawSignalEvent>(reader.GetString(0), JsonOptions);
            if (payload is not null)
            {
                results.Add(payload);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<OpportunityWindowEvent>> GetWindowEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT payload_json
            FROM window_events
            WHERE opened_at_utc >= $from_utc AND closed_at_utc <= $to_utc
            ORDER BY opened_at_utc;
            """;

        command.Parameters.AddWithValue("$from_utc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$to_utc", toUtc.ToString("O"));

        var results = new List<OpportunityWindowEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = JsonSerializer.Deserialize<OpportunityWindowEvent>(reader.GetString(0), JsonOptions);
            if (payload is not null)
            {
                results.Add(payload);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<HealthEvent>> GetHealthEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT timestamp_utc, event_type, exchange_id, flags, is_healthy_after_event, message
            FROM health_events
            WHERE timestamp_utc >= $from_utc AND timestamp_utc <= $to_utc
            ORDER BY timestamp_utc;
            """;

        command.Parameters.AddWithValue("$from_utc", fromUtc.ToString("O"));
        command.Parameters.AddWithValue("$to_utc", toUtc.ToString("O"));

        var results = new List<HealthEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HealthEvent(
                DateTimeOffset.Parse(reader.GetString(0)),
                (ArbiScan.Core.Enums.HealthEventType)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : (ArbiScan.Core.Enums.ExchangeId)reader.GetInt32(2),
                (ArbiScan.Core.Enums.DataHealthFlags)reader.GetInt32(3),
                reader.GetInt32(4) == 1,
                reader.GetString(5)));
        }

        return results;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
