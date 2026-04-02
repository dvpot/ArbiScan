using System.Data;
using System.Text.Json;
using ArbiScan.Core.Models;
using ArbiScan.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace ArbiScan.Infrastructure.Persistence;

public sealed class SqliteOpportunityRepository : IOpportunityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
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
            CREATE TABLE IF NOT EXISTS orderbook_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                exchange_id INTEGER NOT NULL,
                symbol TEXT NOT NULL,
                status INTEGER NOT NULL,
                data_age_ms INTEGER NOT NULL,
                best_bid_price REAL NULL,
                best_bid_quantity REAL NULL,
                best_ask_price REAL NULL,
                best_ask_quantity REAL NULL,
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
                opened_at_utc TEXT NOT NULL,
                closed_at_utc TEXT NOT NULL,
                lifetime_ms INTEGER NOT NULL,
                symbol TEXT NOT NULL,
                direction INTEGER NOT NULL,
                buy_exchange TEXT NOT NULL,
                sell_exchange TEXT NOT NULL,
                test_notional_usd REAL NOT NULL,
                fillability_status INTEGER NOT NULL,
                executable_quantity REAL NOT NULL,
                fillable_base_quantity REAL NOT NULL,
                binance_best_bid REAL NOT NULL,
                binance_best_ask REAL NOT NULL,
                bybit_best_bid REAL NOT NULL,
                bybit_best_ask REAL NOT NULL,
                gross_pnl_usd REAL NOT NULL,
                optimistic_net_pnl_usd REAL NOT NULL,
                conservative_net_pnl_usd REAL NOT NULL,
                gross_edge_pct REAL NOT NULL,
                optimistic_net_edge_pct REAL NOT NULL,
                conservative_net_edge_pct REAL NOT NULL,
                fees_total_usd REAL NOT NULL,
                buffers_total_usd REAL NOT NULL,
                health_flags INTEGER NOT NULL,
                notes TEXT NULL
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
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveOrderBookSnapshotAsync(OrderBookSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO orderbook_snapshots (
                timestamp_utc, exchange_id, symbol, status, data_age_ms, best_bid_price, best_bid_quantity, best_ask_price, best_ask_quantity, payload_json
            )
            VALUES (
                $timestamp_utc, $exchange_id, $symbol, $status, $data_age_ms, $best_bid_price, $best_bid_quantity, $best_ask_price, $best_ask_quantity, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$timestamp_utc", snapshot.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$exchange_id", (int)snapshot.Exchange);
        command.Parameters.AddWithValue("$symbol", snapshot.Symbol);
        command.Parameters.AddWithValue("$status", (int)snapshot.Status);
        command.Parameters.AddWithValue("$data_age_ms", (long)snapshot.DataAge.TotalMilliseconds);
        command.Parameters.AddWithValue("$best_bid_price", (object?)snapshot.BestBidPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$best_bid_quantity", (object?)snapshot.BestBidQuantity ?? DBNull.Value);
        command.Parameters.AddWithValue("$best_ask_price", (object?)snapshot.BestAskPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("$best_ask_quantity", (object?)snapshot.BestAskQuantity ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", snapshot.PayloadJson);
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
            )
            VALUES (
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
                window_id, opened_at_utc, closed_at_utc, lifetime_ms, symbol, direction, buy_exchange, sell_exchange, test_notional_usd,
                fillability_status, executable_quantity, fillable_base_quantity, binance_best_bid, binance_best_ask, bybit_best_bid, bybit_best_ask,
                gross_pnl_usd, optimistic_net_pnl_usd, conservative_net_pnl_usd, gross_edge_pct, optimistic_net_edge_pct, conservative_net_edge_pct,
                fees_total_usd, buffers_total_usd, health_flags, notes
            )
            VALUES (
                $window_id, $opened_at_utc, $closed_at_utc, $lifetime_ms, $symbol, $direction, $buy_exchange, $sell_exchange, $test_notional_usd,
                $fillability_status, $executable_quantity, $fillable_base_quantity, $binance_best_bid, $binance_best_ask, $bybit_best_bid, $bybit_best_ask,
                $gross_pnl_usd, $optimistic_net_pnl_usd, $conservative_net_pnl_usd, $gross_edge_pct, $optimistic_net_edge_pct, $conservative_net_edge_pct,
                $fees_total_usd, $buffers_total_usd, $health_flags, $notes
            );
            """;

        command.Parameters.AddWithValue("$window_id", windowEvent.WindowId);
        command.Parameters.AddWithValue("$opened_at_utc", windowEvent.OpenedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$closed_at_utc", windowEvent.ClosedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$lifetime_ms", windowEvent.LifetimeMs);
        command.Parameters.AddWithValue("$symbol", windowEvent.Symbol);
        command.Parameters.AddWithValue("$direction", (int)windowEvent.Direction);
        command.Parameters.AddWithValue("$buy_exchange", windowEvent.BuyExchange);
        command.Parameters.AddWithValue("$sell_exchange", windowEvent.SellExchange);
        command.Parameters.AddWithValue("$test_notional_usd", windowEvent.TestNotionalUsd);
        command.Parameters.AddWithValue("$fillability_status", (int)windowEvent.FillabilityStatus);
        command.Parameters.AddWithValue("$executable_quantity", windowEvent.ExecutableQuantity);
        command.Parameters.AddWithValue("$fillable_base_quantity", windowEvent.FillableBaseQuantity);
        command.Parameters.AddWithValue("$binance_best_bid", windowEvent.BinanceBestBid);
        command.Parameters.AddWithValue("$binance_best_ask", windowEvent.BinanceBestAsk);
        command.Parameters.AddWithValue("$bybit_best_bid", windowEvent.BybitBestBid);
        command.Parameters.AddWithValue("$bybit_best_ask", windowEvent.BybitBestAsk);
        command.Parameters.AddWithValue("$gross_pnl_usd", windowEvent.GrossPnlUsd);
        command.Parameters.AddWithValue("$optimistic_net_pnl_usd", windowEvent.OptimisticNetPnlUsd);
        command.Parameters.AddWithValue("$conservative_net_pnl_usd", windowEvent.ConservativeNetPnlUsd);
        command.Parameters.AddWithValue("$gross_edge_pct", windowEvent.GrossEdgePct);
        command.Parameters.AddWithValue("$optimistic_net_edge_pct", windowEvent.OptimisticNetEdgePct);
        command.Parameters.AddWithValue("$conservative_net_edge_pct", windowEvent.ConservativeNetEdgePct);
        command.Parameters.AddWithValue("$fees_total_usd", windowEvent.FeesTotalUsd);
        command.Parameters.AddWithValue("$buffers_total_usd", windowEvent.BuffersTotalUsd);
        command.Parameters.AddWithValue("$health_flags", (int)windowEvent.HealthFlags);
        command.Parameters.AddWithValue("$notes", (object?)windowEvent.Notes ?? DBNull.Value);
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
            )
            VALUES (
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

    public async Task<IReadOnlyList<OpportunityWindowEvent>> GetWindowEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                window_id, opened_at_utc, closed_at_utc, lifetime_ms, symbol, direction, buy_exchange, sell_exchange, test_notional_usd,
                fillability_status, executable_quantity, fillable_base_quantity, binance_best_bid, binance_best_ask, bybit_best_bid, bybit_best_ask,
                gross_pnl_usd, optimistic_net_pnl_usd, conservative_net_pnl_usd, gross_edge_pct, optimistic_net_edge_pct, conservative_net_edge_pct,
                fees_total_usd, buffers_total_usd, health_flags, notes
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
            results.Add(new OpportunityWindowEvent(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetString(4),
                (ArbiScan.Core.Enums.ArbitrageDirection)reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDecimal(8),
                (ArbiScan.Core.Enums.FillabilityStatus)reader.GetInt32(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetDecimal(12),
                reader.GetDecimal(13),
                reader.GetDecimal(14),
                reader.GetDecimal(15),
                reader.GetDecimal(16),
                reader.GetDecimal(17),
                reader.GetDecimal(18),
                reader.GetDecimal(19),
                reader.GetDecimal(20),
                reader.GetDecimal(21),
                reader.GetDecimal(22),
                reader.GetDecimal(23),
                (ArbiScan.Core.Enums.DataHealthFlags)reader.GetInt32(24),
                reader.IsDBNull(25) ? null : reader.GetString(25)));
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

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
