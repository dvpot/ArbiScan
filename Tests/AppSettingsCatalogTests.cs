using System.ComponentModel.DataAnnotations;
using ArbiScan.Infrastructure.Setup;
using ArbiScan.Scanner;

namespace ArbiScan.Tests;

public sealed class AppSettingsCatalogTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "arbiscan-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndActivatePreset_Works()
    {
        var catalog = CreateCatalog();
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "config", "appsettings.json"), ExampleAppSettingsJson("XRPUSDT"));

        var presetName = await catalog.SaveCurrentAsPresetAsync("xrp", CancellationToken.None);
        Assert.Equal("xrp", presetName);

        await catalog.UpsertPresetAsync("doge", ExampleAppSettingsJson("DOGEUSDT"), CancellationToken.None);
        await catalog.ActivatePresetAsync("doge", CancellationToken.None);

        var currentJson = await catalog.GetCurrentSettingsAsync(CancellationToken.None);
        Assert.Contains("DOGEUSDT", currentJson, StringComparison.Ordinal);
        Assert.Equal("doge", await catalog.GetMatchingPresetNameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PatchCurrentSettings_UpdatesJsonAndValidates()
    {
        var catalog = CreateCatalog();
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "config", "appsettings.json"), ExampleAppSettingsJson("XRPUSDT"));

        await catalog.PatchCurrentSettingsAsync("Symbol", "\"TRXUSDT\"", CancellationToken.None);
        await catalog.PatchCurrentSettingsAsync("TestNotionalsUsd", "[10,20,50]", CancellationToken.None);

        var currentJson = await catalog.GetCurrentSettingsAsync(CancellationToken.None);
        Assert.Contains("TRXUSDT", currentJson, StringComparison.Ordinal);
        Assert.Contains("[\n      10,\n      20,\n      50\n    ]", currentJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertPreset_RejectsInvalidSettings()
    {
        var catalog = CreateCatalog();
        var invalidJson =
            """
            {
              "ArbiScan": {
                "Symbol": "XRPUSDT",
                "BaseAsset": "XRP",
                "QuoteAsset": "USDT",
                "ScanIntervalMs": 10,
                "TestNotionalsUsd": [100],
                "Storage": {
                  "RootPath": "/app/storage",
                  "ConfigDirectoryName": "config",
                  "LogsDirectoryName": "logs",
                  "DataDirectoryName": "data",
                  "ReportsDirectoryName": "reports",
                  "DatabaseFileName": "arbiscan.sqlite"
                },
                "Binance": {},
                "Bybit": {}
              }
            }
            """;

        await Assert.ThrowsAsync<ValidationException>(() => catalog.UpsertPresetAsync("broken", invalidJson, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private AppSettingsCatalog CreateCatalog()
    {
        var paths = StoragePathBuilder.Build(new Core.Configuration.StorageSettings
        {
            RootPath = _rootPath
        });

        return new AppSettingsCatalog(paths);
    }

    private static string ExampleAppSettingsJson(string symbol) =>
        $$"""
        {
          "ArbiScan": {
            "Symbol": "{{symbol}}",
            "BaseAsset": "{{symbol.Replace("USDT", string.Empty, StringComparison.Ordinal)}}",
            "QuoteAsset": "USDT",
            "ScanIntervalMs": 250,
            "QuoteStalenessThresholdMs": 3000,
            "CumulativeSummaryIntervalSeconds": 3600,
            "TestNotionalsUsd": [10, 20, 50],
            "BinanceTakerFeeRate": 0.001,
            "BybitTakerFeeRate": 0.001,
            "SafetyBufferBps": 2,
            "EntryThresholdUsd": 0,
            "EntryThresholdBps": 0,
            "RawSignalJsonExportMode": "PositiveOnly",
            "Storage": {
              "RootPath": "/app/storage",
              "ConfigDirectoryName": "config",
              "LogsDirectoryName": "logs",
              "DataDirectoryName": "data",
              "ReportsDirectoryName": "reports",
              "DatabaseFileName": "arbiscan.sqlite"
            },
            "Binance": {},
            "Bybit": {}
          }
        }
        """;
}
