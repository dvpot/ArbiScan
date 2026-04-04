using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using ArbiScan.Core.Services;
using ArbiScan.Exchanges.Binance;
using ArbiScan.Exchanges.Bybit;
using ArbiScan.Infrastructure.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArbiScan.Scanner;

public sealed class ScannerWorker : BackgroundService
{
    private readonly AppSettings _settings;
    private readonly AppStoragePaths _storagePaths;
    private readonly BinanceSpotExchangeAdapter _binanceAdapter;
    private readonly BybitSpotExchangeAdapter _bybitAdapter;
    private readonly SignalCalculator _signalCalculator;
    private readonly OpportunityLifetimeTracker _windowTracker;
    private readonly IOpportunityRepository _repository;
    private readonly IReportExporter _exporter;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly IHealthReportGenerator _healthReportGenerator;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly TelegramSettings _telegramSettings;
    private readonly ILogger<ScannerWorker> _logger;

    private readonly Dictionary<ExchangeId, bool> _wasHealthyByExchange = new();
    private readonly Dictionary<ExchangeId, bool> _wasStaleByExchange = new();
    private DateTimeOffset _startedAtUtc;

    public ScannerWorker(
        AppSettings settings,
        AppStoragePaths storagePaths,
        BinanceSpotExchangeAdapter binanceAdapter,
        BybitSpotExchangeAdapter bybitAdapter,
        SignalCalculator signalCalculator,
        OpportunityLifetimeTracker windowTracker,
        IOpportunityRepository repository,
        IReportExporter exporter,
        ISummaryGenerator summaryGenerator,
        IHealthReportGenerator healthReportGenerator,
        ITelegramNotifier telegramNotifier,
        TelegramSettings telegramSettings,
        ILogger<ScannerWorker> logger)
    {
        _settings = settings;
        _storagePaths = storagePaths;
        _binanceAdapter = binanceAdapter;
        _bybitAdapter = bybitAdapter;
        _signalCalculator = signalCalculator;
        _windowTracker = windowTracker;
        _repository = repository;
        _exporter = exporter;
        _summaryGenerator = summaryGenerator;
        _healthReportGenerator = healthReportGenerator;
        _telegramNotifier = telegramNotifier;
        _telegramSettings = telegramSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedAtUtc = DateTimeOffset.UtcNow;
        var nextHourlySummaryAtUtc = RoundUpToHour(_startedAtUtc);
        var nextDailySummaryAtUtc = RoundUpToDay(_startedAtUtc);
        var nextCumulativeSummaryAtUtc = _startedAtUtc.AddSeconds(_settings.CumulativeSummaryIntervalSeconds);
        var nextHeartbeatAtUtc = _startedAtUtc.AddMinutes(_telegramSettings.HeartbeatIntervalMinutes);

        try
        {
            await _repository.InitializeAsync(stoppingToken);
            await PersistHealthEventAsync(new HealthEvent(_startedAtUtc, HealthEventType.ApplicationStarted, null, DataHealthFlags.None, true, $"ArbiScan v2 started ({AppVersion.Current})"), stoppingToken);
            await NotifyStartupAsync(stoppingToken);

            await _binanceAdapter.InitializeAsync(stoppingToken);
            await _bybitAdapter.InitializeAsync(stoppingToken);
            await _binanceAdapter.StartAsync(stoppingToken);
            await _bybitAdapter.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var marketSnapshot = BuildMarketSnapshot(nowUtc);

                await DetectHealthTransitionsAsync(marketSnapshot, stoppingToken);
                await EvaluateSignalsAsync(marketSnapshot, stoppingToken);

                if (nowUtc >= nextHourlySummaryAtUtc)
                {
                    await GenerateReportsAsync(SummaryPeriod.Hourly, nextHourlySummaryAtUtc.AddHours(-1), nextHourlySummaryAtUtc, stoppingToken);
                    nextHourlySummaryAtUtc = nextHourlySummaryAtUtc.AddHours(1);
                }

                if (nowUtc >= nextDailySummaryAtUtc)
                {
                    await GenerateReportsAsync(SummaryPeriod.Daily, nextDailySummaryAtUtc.AddDays(-1), nextDailySummaryAtUtc, stoppingToken);
                    nextDailySummaryAtUtc = nextDailySummaryAtUtc.AddDays(1);
                }

                if (nowUtc >= nextCumulativeSummaryAtUtc)
                {
                    await GenerateReportsAsync(SummaryPeriod.Cumulative, _startedAtUtc, nowUtc, stoppingToken);
                    nextCumulativeSummaryAtUtc = nowUtc.AddSeconds(_settings.CumulativeSummaryIntervalSeconds);
                }

                if (_telegramNotifier.IsEnabled && nowUtc >= nextHeartbeatAtUtc)
                {
                    await _telegramNotifier.SendMessageAsync(BuildHeartbeatMessage(marketSnapshot), stoppingToken);
                    nextHeartbeatAtUtc = nowUtc.AddMinutes(_telegramSettings.HeartbeatIntervalMinutes);
                }

                await Task.Delay(_settings.ScanIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "ScannerWorker failed");
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeError, null, DataHealthFlags.None, false, $"Critical scanner error: {ex.Message}"), CancellationToken.None);
            if (_telegramSettings.NotifyOnCriticalError)
            {
                await _telegramNotifier.SendMessageAsync($"ArbiScan v2 {AppVersion.Current} critical error: {ex.Message}", CancellationToken.None);
            }

            throw;
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private MarketDataSnapshot BuildMarketSnapshot(DateTimeOffset nowUtc)
    {
        var binance = _binanceAdapter.GetSnapshot();
        var bybit = _bybitAdapter.GetSnapshot();
        var flags = DataHealthFlags.None;

        if (binance is null || !binance.HasQuote)
        {
            flags |= DataHealthFlags.BinanceMissing | DataHealthFlags.BinanceUnhealthy;
        }
        else if (binance.DataAge.TotalMilliseconds > _settings.QuoteStalenessThresholdMs)
        {
            flags |= DataHealthFlags.BinanceStale | DataHealthFlags.BinanceUnhealthy;
        }

        if (bybit is null || !bybit.HasQuote)
        {
            flags |= DataHealthFlags.BybitMissing | DataHealthFlags.BybitUnhealthy;
        }
        else if (bybit.DataAge.TotalMilliseconds > _settings.QuoteStalenessThresholdMs)
        {
            flags |= DataHealthFlags.BybitStale | DataHealthFlags.BybitUnhealthy;
        }

        return new MarketDataSnapshot(_settings.Symbol, nowUtc, binance, bybit, flags);
    }

    private async Task EvaluateSignalsAsync(MarketDataSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var direction in Enum.GetValues<ArbitrageDirection>())
        {
            foreach (var notional in _settings.TestNotionalsUsd.OrderBy(x => x))
            {
                var evaluation = _signalCalculator.Evaluate(snapshot, direction, notional, _settings);
                var signalEvent = ToRawSignalEvent(snapshot, evaluation);
                await _repository.SaveRawSignalEventAsync(signalEvent, cancellationToken);
                await _exporter.ExportRawSignalEventAsync(signalEvent, cancellationToken);

                foreach (var window in _windowTracker.Process(signalEvent))
                {
                    await _repository.SaveWindowEventAsync(window, cancellationToken);
                    await _exporter.ExportWindowEventAsync(window, cancellationToken);
                }
            }
        }
    }

    private async Task DetectHealthTransitionsAsync(MarketDataSnapshot snapshot, CancellationToken cancellationToken)
    {
        await CheckExchangeHealthAsync(snapshot.Binance, DataHealthFlags.BinanceStale, ExchangeId.Binance, cancellationToken);
        await CheckExchangeHealthAsync(snapshot.Bybit, DataHealthFlags.BybitStale, ExchangeId.Bybit, cancellationToken);
    }

    private async Task CheckExchangeHealthAsync(
        ExchangeMarketSnapshot? exchangeSnapshot,
        DataHealthFlags staleFlag,
        ExchangeId exchange,
        CancellationToken cancellationToken)
    {
        var isHealthy = exchangeSnapshot is not null &&
                        exchangeSnapshot.HasQuote &&
                        exchangeSnapshot.DataAge.TotalMilliseconds <= _settings.QuoteStalenessThresholdMs;
        var isStale = exchangeSnapshot is not null &&
                      exchangeSnapshot.HasQuote &&
                      exchangeSnapshot.DataAge.TotalMilliseconds > _settings.QuoteStalenessThresholdMs;

        if (!_wasHealthyByExchange.TryGetValue(exchange, out var previousHealthy))
        {
            _wasHealthyByExchange[exchange] = isHealthy;
            _wasStaleByExchange[exchange] = isStale;
            if (isHealthy)
            {
                await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeConnected, exchange, DataHealthFlags.None, true, $"{exchange} quote stream connected"), cancellationToken);
            }

            return;
        }

        if (!_wasStaleByExchange.TryGetValue(exchange, out var previousStale))
        {
            previousStale = false;
        }

        if (!previousStale && isStale)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.StaleQuotesDetected, exchange, staleFlag, false, $"{exchange} quotes became stale"), cancellationToken);
            _logger.LogWarning("{Exchange} quotes became stale", exchange);
        }
        else if (previousStale && !isStale)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.StaleQuotesRecovered, exchange, DataHealthFlags.None, isHealthy, $"{exchange} quotes recovered from stale"), cancellationToken);
            _logger.LogInformation("{Exchange} quotes recovered from stale", exchange);
        }

        if (!previousHealthy && isHealthy)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeRecovered, exchange, DataHealthFlags.None, true, $"{exchange} quote stream recovered"), cancellationToken);
        }

        _wasHealthyByExchange[exchange] = isHealthy;
        _wasStaleByExchange[exchange] = isStale;
    }

    private async Task GenerateReportsAsync(SummaryPeriod period, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var signals = await _repository.GetRawSignalEventsAsync(fromUtc, toUtc, cancellationToken);
        var windows = await _repository.GetWindowEventsAsync(fromUtc, toUtc, cancellationToken);
        var healthEvents = await _repository.GetHealthEventsAsync(fromUtc, toUtc, cancellationToken);

        var summary = _summaryGenerator.Generate(period, fromUtc, toUtc, _settings.Symbol, signals, windows);
        var health = _healthReportGenerator.Generate(period, fromUtc, toUtc, _settings.Symbol, _startedAtUtc, healthEvents);

        await _repository.SaveSummaryAsync(summary, cancellationToken);
        await _exporter.ExportSummaryAsync(summary, cancellationToken);
        await _repository.SaveHealthReportAsync(health, cancellationToken);
        await _exporter.ExportHealthReportAsync(health, cancellationToken);
    }

    private static RawSignalEvent ToRawSignalEvent(MarketDataSnapshot snapshot, OpportunityEvaluation evaluation) =>
        new(
            evaluation.TimestampUtc,
            snapshot.Symbol,
            evaluation.Direction,
            evaluation.TestNotionalUsd,
            snapshot.Binance?.BestBidPrice ?? 0m,
            snapshot.Binance?.BestAskPrice ?? 0m,
            snapshot.Bybit?.BestBidPrice ?? 0m,
            snapshot.Bybit?.BestAskPrice ?? 0m,
            evaluation.GrossSpreadUsd,
            evaluation.GrossSpreadBps,
            evaluation.BuyFeeUsd,
            evaluation.SellFeeUsd,
            evaluation.TotalFeesUsd,
            evaluation.SafetyBufferUsd,
            evaluation.NetEdgeUsd,
            evaluation.NetEdgeBps,
            evaluation.ExpectedNetPnlUsd,
            evaluation.ExpectedNetPnlPct,
            evaluation.SignalClass,
            evaluation.HealthFlags);

    private async Task PersistHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken)
    {
        await _repository.SaveHealthEventAsync(healthEvent, cancellationToken);
        await _exporter.ExportHealthEventAsync(healthEvent, cancellationToken);

        if (_telegramNotifier.IsEnabled &&
            _telegramSettings.NotifyOnHealthStateChanges &&
            healthEvent.Exchange.HasValue &&
            healthEvent.EventType is HealthEventType.ExchangeRecovered or HealthEventType.StaleQuotesDetected or HealthEventType.StaleQuotesRecovered)
        {
            await _telegramNotifier.SendMessageAsync($"ArbiScan v2 {AppVersion.Current} health: {healthEvent.Message}", cancellationToken);
        }
    }

    private async Task NotifyStartupAsync(CancellationToken cancellationToken)
    {
        if (_telegramNotifier.IsEnabled && _telegramSettings.NotifyOnStartup)
        {
            await _telegramNotifier.SendMessageAsync($"ArbiScan v2 {AppVersion.Current} started for {_settings.Symbol}. Notionals: {string.Join(", ", _settings.TestNotionalsUsd.Select(x => x.ToString("0.########")))} USD.", cancellationToken);
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            foreach (var window in _windowTracker.FlushAll(DateTimeOffset.UtcNow))
            {
                await _repository.SaveWindowEventAsync(window, CancellationToken.None);
                await _exporter.ExportWindowEventAsync(window, CancellationToken.None);
            }

            await _binanceAdapter.StopAsync(CancellationToken.None);
            await _bybitAdapter.StopAsync(CancellationToken.None);
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ApplicationStopping, null, DataHealthFlags.None, true, $"ArbiScan v2 stopping ({AppVersion.Current})"), CancellationToken.None);

            if (_telegramNotifier.IsEnabled && _telegramSettings.NotifyOnShutdown)
            {
                await _telegramNotifier.SendMessageAsync($"ArbiScan v2 {AppVersion.Current} stopped for {_settings.Symbol}.", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutdown failed");
        }
    }

    private string BuildHeartbeatMessage(MarketDataSnapshot snapshot)
    {
        var binance = snapshot.Binance;
        var bybit = snapshot.Bybit;
        return $"ArbiScan v2 heartbeat {AppVersion.Current} {_settings.Symbol}\n" +
               $"Binance bid/ask: {binance?.BestBidPrice:0.########}/{binance?.BestAskPrice:0.########}, age={binance?.DataAge.TotalMilliseconds:0} ms\n" +
               $"Bybit bid/ask: {bybit?.BestBidPrice:0.########}/{bybit?.BestAskPrice:0.########}, age={bybit?.DataAge.TotalMilliseconds:0} ms\n" +
               $"Health flags: {snapshot.HealthFlags}";
    }

    private static DateTimeOffset RoundUpToHour(DateTimeOffset timestampUtc)
    {
        var hourStart = new DateTimeOffset(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, timestampUtc.Hour, 0, 0, TimeSpan.Zero);
        return hourStart <= timestampUtc ? hourStart.AddHours(1) : hourStart;
    }

    private static DateTimeOffset RoundUpToDay(DateTimeOffset timestampUtc)
    {
        var dayStart = new DateTimeOffset(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, 0, 0, 0, TimeSpan.Zero);
        return dayStart <= timestampUtc ? dayStart.AddDays(1) : dayStart;
    }
}
