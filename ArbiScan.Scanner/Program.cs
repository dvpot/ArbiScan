using System.ComponentModel.DataAnnotations;
using ArbiScan.Core.Configuration;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Services;
using ArbiScan.Exchanges.Binance;
using ArbiScan.Exchanges.Bybit;
using ArbiScan.Infrastructure.Logging;
using ArbiScan.Infrastructure.Persistence;
using ArbiScan.Infrastructure.Reporting;
using ArbiScan.Infrastructure.Setup;
using ArbiScan.Scanner;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();
var provisionalStorageRoot = builder.Configuration["ArbiScan:Storage:RootPath"] ?? "/app/storage";
builder.Configuration.AddJsonFile(Path.Combine(provisionalStorageRoot, "config", "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(provisionalStorageRoot, "config", "telegramsettings.json"), optional: true, reloadOnChange: true);

var settings = new AppSettings();
builder.Configuration.GetSection("ArbiScan").Bind(settings);
ValidateSettings(settings);
var telegramSettings = new TelegramSettings();
builder.Configuration.GetSection("TelegramBot").Bind(telegramSettings);
ValidateTelegramSettings(telegramSettings);

var storagePaths = StoragePathBuilder.Build(settings.Storage);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.SingleLine = true;
});
builder.Logging.AddProvider(new RollingFileLoggerProvider(storagePaths.LogsPath, LogLevel.Information));

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(telegramSettings);
builder.Services.AddSingleton(storagePaths);
builder.Services.AddSingleton<IFeeCalculator>(_ => new FeeCalculator(settings.BinanceTakerFeeRate, settings.BybitTakerFeeRate));
builder.Services.AddSingleton<IFillableSizeCalculator, FillableSizeCalculator>();
builder.Services.AddSingleton<IOpportunityDetector, OpportunityDetector>();
builder.Services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
builder.Services.AddSingleton<OpportunityLifetimeTracker>();
builder.Services.AddSingleton<IOpportunityRepository>(_ => new SqliteOpportunityRepository(storagePaths.DatabasePath));
builder.Services.AddSingleton<IReportExporter>(_ => new JsonReportExporter(storagePaths.ReportsPath));
builder.Services.AddSingleton<ITelegramNotifier>(sp =>
    telegramSettings.Enabled
        ? new TelegramBotNotifier(telegramSettings, sp.GetRequiredService<ILogger<TelegramBotNotifier>>())
        : new NullTelegramNotifier());
builder.Services.AddSingleton(sp => new BinanceSpotExchangeAdapter(
    settings.Symbol,
    settings.OrderBookDepth,
    settings.RuntimeMode,
    settings.Binance,
    sp.GetRequiredService<ILogger<BinanceSpotExchangeAdapter>>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new BybitSpotExchangeAdapter(
    settings.Symbol,
    settings.OrderBookDepth,
    settings.RuntimeMode,
    settings.Bybit,
    sp.GetRequiredService<ILogger<BybitSpotExchangeAdapter>>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<ScannerWorker>();

var host = builder.Build();
await host.RunAsync();

static void ValidateSettings(AppSettings settings)
{
    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(settings);
    if (!Validator.TryValidateObject(settings, validationContext, validationResults, true))
    {
        throw new ValidationException(string.Join(Environment.NewLine, validationResults.Select(x => x.ErrorMessage)));
    }

    if (settings.TestNotionalsUsd.Any(x => x <= 0m))
    {
        throw new ValidationException("All test notionals must be positive.");
    }
}

static void ValidateTelegramSettings(TelegramSettings settings)
{
    if (!settings.Enabled)
    {
        return;
    }

    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(settings);
    if (!Validator.TryValidateObject(settings, validationContext, validationResults, true))
    {
        throw new ValidationException(string.Join(Environment.NewLine, validationResults.Select(x => x.ErrorMessage)));
    }

    if (settings.AllowedUserId == 0)
    {
        throw new ValidationException("TelegramBot:AllowedUserId must be set when Telegram is enabled.");
    }
}
