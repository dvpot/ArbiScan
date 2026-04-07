using System.ComponentModel.DataAnnotations;
using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Configuration;

public sealed class AppSettings
{
    [Required]
    public string Symbol { get; init; } = "TRXUSDT";

    [Required]
    public string BaseAsset { get; init; } = "TRX";

    [Required]
    public string QuoteAsset { get; init; } = "USDT";

    [Range(50, 10_000)]
    public int ScanIntervalMs { get; init; } = 250;

    public bool RestHealthProbeEnabled { get; init; } = true;

    [Range(1, 86_400_000)]
    public int RestHealthProbeAfterMs { get; init; } = 5_000;

    [Range(1, 86_400_000)]
    public int RestHealthProbeCooldownMs { get; init; } = 30_000;

    [Range(60, 86_400)]
    public int CumulativeSummaryIntervalSeconds { get; init; } = 3_600;

    [MinLength(1)]
    public decimal[] TestNotionalsUsd { get; init; } = [100m];

    [Range(0, 1)]
    public decimal BinanceTakerFeeRate { get; init; } = 0.001m;

    [Range(0, 1)]
    public decimal BybitTakerFeeRate { get; init; } = 0.001m;

    [Range(0, 10_000)]
    public decimal SafetyBufferBps { get; init; } = 2m;

    [Range(-1_000_000, 1_000_000)]
    public decimal EntryThresholdUsd { get; init; } = 0m;

    [Range(-10_000, 10_000)]
    public decimal EntryThresholdBps { get; init; } = 0m;

    public RawSignalJsonExportMode RawSignalJsonExportMode { get; init; } = RawSignalJsonExportMode.PositiveOnly;

    [Required]
    public StorageSettings Storage { get; init; } = new();

    [Required]
    public ExchangeConnectionSettings Binance { get; init; } = new();

    [Required]
    public ExchangeConnectionSettings Bybit { get; init; } = new();
}

public sealed class StorageSettings
{
    [Required]
    public string RootPath { get; init; } = "/app/storage";

    [Required]
    public string ConfigDirectoryName { get; init; } = "config";

    [Required]
    public string LogsDirectoryName { get; init; } = "logs";

    [Required]
    public string DataDirectoryName { get; init; } = "data";

    [Required]
    public string ReportsDirectoryName { get; init; } = "reports";

    [Required]
    public string DatabaseFileName { get; init; } = "arbiscan.sqlite";
}

public sealed class TelegramSettings
{
    public bool Enabled { get; init; }

    [Required]
    public string BotToken { get; init; } = string.Empty;

    public long AllowedUserId { get; init; }

    [Range(1, 1440)]
    public int HeartbeatIntervalMinutes { get; init; } = 30;

    [Range(0, 1440)]
    public int HealthStateNotificationCooldownMinutes { get; init; } = 30;

    public bool NotifyOnStartup { get; init; } = true;

    public bool NotifyOnShutdown { get; init; } = true;

    public bool NotifyOnCriticalError { get; init; } = true;

    public bool NotifyOnHealthStateChanges { get; init; } = true;

    public bool NotifyOnSignalLifecycle { get; init; } = true;

    public bool NotifyOnSignalNewMax { get; init; }
}

public sealed class ExchangeConnectionSettings
{
    public string? ApiKey { get; init; }
    public string? ApiSecret { get; init; }
}
