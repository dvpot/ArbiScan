using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;
using ArbiScan.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace ArbiScan.Tests;

public sealed class SqliteOpportunityRepositoryTests
{
    [Fact]
    public async Task InitializeAsync_RecreatesIncompatibleWindowEventsSchema()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "arbiscan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "arbiscan.sqlite");

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE window_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    window_id TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    direction INTEGER NOT NULL,
                    test_notional_usd REAL NOT NULL,
                    opened_at_utc TEXT NOT NULL,
                    closed_at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteOpportunityRepository(databasePath);
        await repository.InitializeAsync(CancellationToken.None);
        await repository.SaveWindowEventAsync(
            new OpportunityWindowEvent(
                "window-1",
                "XRPUSDT",
                ArbitrageDirection.BuyBinanceSellBybit,
                100m,
                DateTimeOffset.UtcNow.AddSeconds(-5),
                DateTimeOffset.UtcNow,
                5000,
                0.4m,
                0.2m,
                0.15m,
                3,
                SignalClass.EntryQualified),
            CancellationToken.None);

        var windows = await repository.GetWindowEventsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Single(windows);

        Directory.Delete(tempRoot, recursive: true);
    }
}
