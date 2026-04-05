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
builder.Configuration.AddJsonFile("config/appsettings.example.json", optional: true, reloadOnChange: false);
builder.Configuration.AddJsonFile("config/telegramsettings.example.json", optional: true, reloadOnChange: false);
var provisionalStorageRoot = builder.Configuration["ArbiScan:Storage:RootPath"] ?? "/app/storage";
builder.Configuration.AddJsonFile(Path.Combine(provisionalStorageRoot, "config", "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(provisionalStorageRoot, "config", "telegramsettings.json"), optional: true, reloadOnChange: true);

var settings = new AppSettings();
builder.Configuration.GetSection("ArbiScan").Bind(settings);
SettingsValidator.ValidateAppSettings(settings);
var telegramSettings = new TelegramSettings();
builder.Configuration.GetSection("TelegramBot").Bind(telegramSettings);
SettingsValidator.ValidateTelegramSettings(telegramSettings);

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
builder.Services.AddSingleton<RuntimeStateTracker>();
builder.Services.AddSingleton<AppSettingsCatalog>();
builder.Services.AddSingleton<IFeeCalculator>(_ => new FeeCalculator(settings.BinanceTakerFeeRate, settings.BybitTakerFeeRate));
builder.Services.AddSingleton<SignalCalculator>();
builder.Services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
builder.Services.AddSingleton<IHealthReportGenerator, HealthReportGenerator>();
builder.Services.AddSingleton<OpportunityLifetimeTracker>();
builder.Services.AddSingleton<IOpportunityRepository>(_ => new SqliteOpportunityRepository(storagePaths.DatabasePath));
builder.Services.AddSingleton<IReportExporter>(_ => new JsonReportExporter(storagePaths.ReportsPath));

if (telegramSettings.Enabled)
{
    builder.Services.AddSingleton<TelegramBotNotifier>();
    builder.Services.AddSingleton<ITelegramNotifier>(sp => sp.GetRequiredService<TelegramBotNotifier>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotNotifier>());
}
else
{
    builder.Services.AddSingleton<ITelegramNotifier>(_ => new NullTelegramNotifier());
}

builder.Services.AddSingleton(sp => new BinanceSpotExchangeAdapter(
    settings.Symbol,
    settings.Binance,
    sp.GetRequiredService<ILogger<BinanceSpotExchangeAdapter>>()));
builder.Services.AddSingleton(sp => new BybitSpotExchangeAdapter(
    settings.Symbol,
    settings.Bybit,
    sp.GetRequiredService<ILogger<BybitSpotExchangeAdapter>>()));
builder.Services.AddHostedService<ScannerWorker>();
builder.Services.AddHostedService<TelegramControlBotService>();

var host = builder.Build();
await host.RunAsync();
