using System.ComponentModel.DataAnnotations;

namespace ArbiScan.Core.Configuration;

public sealed class AppSettings
{
    [Required]
    public string Symbol { get; init; } = "TRXUSDT";

    [Required]
    public string BaseAsset { get; init; } = "TRX";

    [Required]
    public string QuoteAsset { get; init; } = "USDT";

    public RuntimeMode RuntimeMode { get; init; } = RuntimeMode.Mainnet;

    [Range(50, 10_000)]
    public int ScanIntervalMs { get; init; } = 250;

    [Range(1, 86_400_000)]
    public int QuoteStalenessThresholdMs { get; init; } = 2_000;

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

    public bool NotifyOnStartup { get; init; } = true;

    public bool NotifyOnShutdown { get; init; } = true;

    public bool NotifyOnCriticalError { get; init; } = true;

    public bool NotifyOnHealthStateChanges { get; init; } = true;
}

public sealed class ExchangeConnectionSettings
{
    public string? ApiKey { get; init; }
    public string? ApiSecret { get; init; }
}

public enum RuntimeMode
{
    Mainnet = 0,
    Testnet = 1
}
