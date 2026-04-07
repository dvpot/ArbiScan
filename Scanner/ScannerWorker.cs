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
    private sealed class HealthNotificationState
    {
        public DateTimeOffset? LastProblemNotificationAtUtc { get; set; }
        public bool HasActiveProblemNotification { get; set; }
    }

    private sealed class RestProbeState
    {
        public DateTimeOffset? LastProbeAtUtc { get; set; }
        public DateTimeOffset? LastObservedUpdateUtc { get; set; }
        public bool? LastSucceeded { get; set; }
        public string? LastFailureReason { get; set; }
    }

    private sealed class SignalLifecycleState
    {
        public required ArbitrageDirection Direction { get; init; }
        public required decimal TestNotionalUsd { get; init; }
        public required SignalClass StrongestSignalClass { get; set; }
        public required decimal MaxNetEdgeUsd { get; set; }
    }

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
    private readonly RuntimeStateTracker _runtimeStateTracker;
    private readonly ILogger<ScannerWorker> _logger;

    private readonly Dictionary<ExchangeId, bool> _wasHealthyByExchange = new();
    private readonly Dictionary<ExchangeId, HealthNotificationState> _healthNotificationStatesByExchange = new();
    private readonly Dictionary<ExchangeId, RestProbeState> _restProbeStatesByExchange = new();
    private readonly Dictionary<string, SignalLifecycleState> _signalLifecycleStates = new(StringComparer.Ordinal);
    private DateTimeOffset _startedAtUtc;
    private string _shutdownReason = "Остановка без уточнённой причины.";

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
        RuntimeStateTracker runtimeStateTracker,
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
        _runtimeStateTracker = runtimeStateTracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedAtUtc = DateTimeOffset.UtcNow;
        _runtimeStateTracker.MarkStarted(_settings.Symbol);
        var nextHourlySummaryAtUtc = RoundUpToHour(_startedAtUtc);
        var nextDailySummaryAtUtc = RoundUpToDay(_startedAtUtc);
        var nextCumulativeSummaryAtUtc = _startedAtUtc.AddSeconds(_settings.CumulativeSummaryIntervalSeconds);
        var nextHeartbeatAtUtc = _startedAtUtc.AddMinutes(_telegramSettings.HeartbeatIntervalMinutes);

        try
        {
            await _repository.InitializeAsync(stoppingToken);
            await PersistHealthEventAsync(new HealthEvent(_startedAtUtc, HealthEventType.ApplicationStarted, null, DataHealthFlags.None, true, $"{AppVersion.ProductName} started"), stoppingToken);
            await NotifyStartupAsync(stoppingToken);

            await _binanceAdapter.InitializeAsync(stoppingToken);
            await _bybitAdapter.InitializeAsync(stoppingToken);
            await _binanceAdapter.StartAsync(stoppingToken);
            await _bybitAdapter.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var marketSnapshot = BuildMarketSnapshot(nowUtc);
                var bestEvaluation = BuildBestEvaluation(marketSnapshot);
                _runtimeStateTracker.UpdateLoop(marketSnapshot, bestEvaluation);

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
            _shutdownReason = "Остановка по запросу хоста или при перезапуске.";
        }
        catch (Exception ex)
        {
            _shutdownReason = $"Критическая ошибка: {ex.Message}";
            _runtimeStateTracker.MarkCriticalError(ex);
            _logger.LogCritical(ex, "ScannerWorker failed");
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeError, null, DataHealthFlags.None, false, $"Критическая ошибка сканера: {ex.Message}"), CancellationToken.None);
            if (_telegramSettings.NotifyOnCriticalError)
            {
                await _telegramNotifier.SendMessageAsync($"{AppVersion.ProductName} критическая ошибка: {ex.Message}", CancellationToken.None);
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
        else if (!binance.IsConnected)
        {
            flags |= DataHealthFlags.BinanceUnhealthy;
        }

        if (bybit is null || !bybit.HasQuote)
        {
            flags |= DataHealthFlags.BybitMissing | DataHealthFlags.BybitUnhealthy;
        }
        else if (!bybit.IsConnected)
        {
            flags |= DataHealthFlags.BybitUnhealthy;
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
                if (ShouldExportRawSignalEvent(signalEvent))
                {
                    await _exporter.ExportRawSignalEventAsync(signalEvent, cancellationToken);
                }

                await HandleSignalLifecycleNotificationAsync(signalEvent, cancellationToken);

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
        await CheckExchangeHealthAsync(snapshot.Binance, ExchangeId.Binance, () => _binanceAdapter.ProbeHealthAsync(cancellationToken), cancellationToken);
        await CheckExchangeHealthAsync(snapshot.Bybit, ExchangeId.Bybit, () => _bybitAdapter.ProbeHealthAsync(cancellationToken), cancellationToken);
    }

    private async Task CheckExchangeHealthAsync(
        ExchangeMarketSnapshot? exchangeSnapshot,
        ExchangeId exchange,
        Func<Task<(bool Success, string? FailureReason)>> probeHealthAsync,
        CancellationToken cancellationToken)
    {
        var (isHealthy, errorMessage) = await EvaluateExchangeHealthAsync(exchangeSnapshot, exchange, probeHealthAsync);

        if (!_wasHealthyByExchange.TryGetValue(exchange, out var previousHealthy))
        {
            _wasHealthyByExchange[exchange] = isHealthy;
            if (isHealthy)
            {
                await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeConnected, exchange, DataHealthFlags.None, true, $"{exchange} quote stream connected"), cancellationToken);
            }

            return;
        }

        if (previousHealthy && !isHealthy)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeError, exchange, BuildExchangeErrorFlags(exchangeSnapshot, exchange), false, errorMessage ?? $"{exchange} quote stream unhealthy"), cancellationToken);
        }
        else if (!previousHealthy && isHealthy)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ExchangeRecovered, exchange, DataHealthFlags.None, true, $"{exchange} quote stream recovered"), cancellationToken);
        }

        _wasHealthyByExchange[exchange] = isHealthy;
    }

    private async Task<(bool IsHealthy, string? ErrorMessage)> EvaluateExchangeHealthAsync(
        ExchangeMarketSnapshot? exchangeSnapshot,
        ExchangeId exchange,
        Func<Task<(bool Success, string? FailureReason)>> probeHealthAsync)
    {
        if (exchangeSnapshot is null || !exchangeSnapshot.HasQuote)
        {
            return (false, $"{exchange} quotes missing");
        }

        if (!exchangeSnapshot.IsConnected)
        {
            return (false, $"{exchange} quote stream disconnected");
        }

        if (!_settings.RestHealthProbeEnabled ||
            exchangeSnapshot.LastUpdateUtc is null ||
            exchangeSnapshot.DataAge.TotalMilliseconds < _settings.RestHealthProbeAfterMs)
        {
            ResetRestProbeState(exchange, exchangeSnapshot.LastUpdateUtc);
            return (true, null);
        }

        var state = GetOrCreateRestProbeState(exchange);
        if (state.LastObservedUpdateUtc != exchangeSnapshot.LastUpdateUtc)
        {
            state.LastObservedUpdateUtc = exchangeSnapshot.LastUpdateUtc;
            state.LastSucceeded = null;
            state.LastFailureReason = null;
            state.LastProbeAtUtc = null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var shouldProbe = !state.LastProbeAtUtc.HasValue ||
                          nowUtc - state.LastProbeAtUtc.Value >= TimeSpan.FromMilliseconds(_settings.RestHealthProbeCooldownMs);
        if (shouldProbe)
        {
            var probeResult = await probeHealthAsync();
            state.LastProbeAtUtc = nowUtc;
            state.LastSucceeded = probeResult.Success;
            state.LastFailureReason = probeResult.FailureReason;
        }

        return state.LastSucceeded == false
            ? (false, state.LastFailureReason ?? $"{exchange} REST health probe failed")
            : (true, null);
    }

    private RestProbeState GetOrCreateRestProbeState(ExchangeId exchange)
    {
        if (_restProbeStatesByExchange.TryGetValue(exchange, out var state))
        {
            return state;
        }

        state = new RestProbeState();
        _restProbeStatesByExchange[exchange] = state;
        return state;
    }

    private void ResetRestProbeState(ExchangeId exchange, DateTimeOffset? lastUpdateUtc)
    {
        var state = GetOrCreateRestProbeState(exchange);
        state.LastObservedUpdateUtc = lastUpdateUtc;
        state.LastSucceeded = null;
        state.LastFailureReason = null;
        state.LastProbeAtUtc = null;
    }

    private static DataHealthFlags BuildExchangeErrorFlags(ExchangeMarketSnapshot? exchangeSnapshot, ExchangeId exchange)
    {
        var flags = exchange switch
        {
            ExchangeId.Binance => DataHealthFlags.BinanceUnhealthy,
            ExchangeId.Bybit => DataHealthFlags.BybitUnhealthy,
            _ => DataHealthFlags.None
        };

        if (exchangeSnapshot is null || !exchangeSnapshot.HasQuote)
        {
            flags |= exchange switch
            {
                ExchangeId.Binance => DataHealthFlags.BinanceMissing,
                ExchangeId.Bybit => DataHealthFlags.BybitMissing,
                _ => DataHealthFlags.None
            };
        }

        return flags;
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
            ShouldNotifyHealthEventInTelegram(healthEvent))
        {
            await _telegramNotifier.SendMessageAsync($"{AppVersion.ProductName} health: {healthEvent.Message}", cancellationToken);
        }
    }

    private bool ShouldNotifyHealthEventInTelegram(HealthEvent healthEvent)
    {
        var exchange = healthEvent.Exchange!.Value;
        if (!_healthNotificationStatesByExchange.TryGetValue(exchange, out var state))
        {
            state = new HealthNotificationState();
            _healthNotificationStatesByExchange[exchange] = state;
        }

        var nowUtc = healthEvent.TimestampUtc;
        var cooldown = TimeSpan.FromMinutes(_telegramSettings.HealthStateNotificationCooldownMinutes);

        switch (healthEvent.EventType)
        {
            case HealthEventType.ExchangeError:
            {
                if (cooldown > TimeSpan.Zero &&
                    state.LastProblemNotificationAtUtc.HasValue &&
                    nowUtc - state.LastProblemNotificationAtUtc.Value < cooldown)
                {
                    state.HasActiveProblemNotification = false;
                    return false;
                }

                state.LastProblemNotificationAtUtc = nowUtc;
                state.HasActiveProblemNotification = true;
                return true;
            }

            case HealthEventType.ExchangeRecovered:
            {
                if (!state.HasActiveProblemNotification)
                {
                    return false;
                }

                state.HasActiveProblemNotification = false;
                return true;
            }

            default:
                return false;
        }
    }

    private async Task NotifyStartupAsync(CancellationToken cancellationToken)
    {
        if (_telegramNotifier.IsEnabled && _telegramSettings.NotifyOnStartup)
        {
            await _telegramNotifier.SendMessageAsync($"{AppVersion.ProductName} запущен для {_settings.Symbol}. Notionals: {string.Join(", ", _settings.TestNotionalsUsd.Select(x => x.ToString("0.########")))} USD.", cancellationToken);
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _runtimeStateTracker.MarkStopping(_shutdownReason);
            foreach (var window in _windowTracker.FlushAll(DateTimeOffset.UtcNow))
            {
                await _repository.SaveWindowEventAsync(window, CancellationToken.None);
                await _exporter.ExportWindowEventAsync(window, CancellationToken.None);
            }

            await _binanceAdapter.StopAsync(CancellationToken.None);
            await _bybitAdapter.StopAsync(CancellationToken.None);
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ApplicationStopping, null, DataHealthFlags.None, true, $"{AppVersion.ProductName} останавливается. Причина: {_shutdownReason}"), CancellationToken.None);

            if (_telegramNotifier.IsEnabled && _telegramSettings.NotifyOnShutdown)
            {
                await _telegramNotifier.SendMessageAsync($"{AppVersion.ProductName} остановлен для {_settings.Symbol}. Причина: {_shutdownReason}", CancellationToken.None);
            }

            _runtimeStateTracker.MarkStopped(_shutdownReason);
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
        var evaluations = BuildHeartbeatEvaluations(snapshot);
        var best = evaluations
            .OrderByDescending(x => (int)x.SignalClass)
            .ThenByDescending(x => x.NetEdgeUsd)
            .ThenByDescending(x => x.GrossSpreadUsd)
            .FirstOrDefault();

        var headline = best is null
            ? "Сейчас нет пригодных котировок."
            : best.SignalClass switch
            {
                SignalClass.EntryQualified => $"Сигнал сейчас: ДА. Лучший entry-qualified сетап: {FormatDirection(best.Direction)} на {best.TestNotionalUsd:0.########} USD.",
                SignalClass.NetPositive => $"Сигнал сейчас: пограничный. Есть net-positive сетап {FormatDirection(best.Direction)} на {best.TestNotionalUsd:0.########} USD, но порог входа ещё не выполнен.",
                SignalClass.FeePositive => $"Сигнал сейчас: НЕТ. Комиссии перекрыты для {FormatDirection(best.Direction)} на {best.TestNotionalUsd:0.########} USD, но safety buffer убирает edge.",
                SignalClass.RawPositive => $"Сигнал сейчас: НЕТ. Есть raw cross-spread для {FormatDirection(best.Direction)} на {best.TestNotionalUsd:0.########} USD, но комиссии его убирают.",
                _ => "Сигнал сейчас: НЕТ. Положительного cross-spread сейчас нет."
            };

        var detail = best is null
            ? "Лучший сетап: недоступен, потому что на одной или обеих биржах сейчас нет пригодных котировок."
            : $"Лучший сетап: {FormatDirection(best.Direction)} | {best.TestNotionalUsd:0.########} USD | gross {best.GrossSpreadUsd:+0.########;-0.########;0} USD ({best.GrossSpreadBps:+0.##;-0.##;0} bps) | fees {best.TotalFeesUsd:0.########} USD | buffer {best.SafetyBufferUsd:0.########} USD | net {best.NetEdgeUsd:+0.########;-0.########;0} USD ({best.NetEdgeBps:+0.##;-0.##;0} bps) | est pnl {best.ExpectedNetPnlUsd:+0.########;-0.########;0} USD.";

        return $"{AppVersion.ProductName} heartbeat | {_settings.Symbol}\n" +
               $"{headline}\n" +
               $"{detail}\n" +
               $"Котировки: Binance {binance?.BestBidPrice:0.########}/{binance?.BestAskPrice:0.########} ({binance?.DataAge.TotalMilliseconds:0} ms), Bybit {bybit?.BestBidPrice:0.########}/{bybit?.BestAskPrice:0.########} ({bybit?.DataAge.TotalMilliseconds:0} ms)\n" +
               $"Health: {snapshot.HealthFlags}";
    }

    private IReadOnlyList<OpportunityEvaluation> BuildHeartbeatEvaluations(MarketDataSnapshot snapshot) =>
        Enum.GetValues<ArbitrageDirection>()
            .SelectMany(direction => _settings.TestNotionalsUsd.Select(notional => _signalCalculator.Evaluate(snapshot, direction, notional, _settings)))
            .Where(x => x.IsQuoteUsable)
            .ToArray();

    private OpportunityEvaluation? BuildBestEvaluation(MarketDataSnapshot snapshot) =>
        BuildHeartbeatEvaluations(snapshot)
            .OrderByDescending(x => (int)x.SignalClass)
            .ThenByDescending(x => x.NetEdgeUsd)
            .ThenByDescending(x => x.GrossSpreadUsd)
            .FirstOrDefault();

    private bool ShouldExportRawSignalEvent(RawSignalEvent signalEvent) =>
        _settings.RawSignalJsonExportMode switch
        {
            RawSignalJsonExportMode.All => true,
            RawSignalJsonExportMode.PositiveOnly => signalEvent.SignalClass >= SignalClass.RawPositive,
            RawSignalJsonExportMode.FeePositiveAndAbove => signalEvent.SignalClass >= SignalClass.FeePositive,
            RawSignalJsonExportMode.NetPositiveAndAbove => signalEvent.SignalClass >= SignalClass.NetPositive,
            RawSignalJsonExportMode.EntryQualifiedOnly => signalEvent.SignalClass == SignalClass.EntryQualified,
            RawSignalJsonExportMode.None => false,
            _ => signalEvent.SignalClass >= SignalClass.RawPositive
        };

    private async Task HandleSignalLifecycleNotificationAsync(RawSignalEvent signalEvent, CancellationToken cancellationToken)
    {
        if (!_telegramNotifier.IsEnabled || !_telegramSettings.NotifyOnSignalLifecycle)
        {
            return;
        }

        var key = CreateSignalKey(signalEvent.Direction, signalEvent.TestNotionalUsd);
        var isOpenSignal = signalEvent.SignalClass >= SignalClass.NetPositive;
        if (isOpenSignal)
        {
            if (!_signalLifecycleStates.TryGetValue(key, out var lifecycleState))
            {
                lifecycleState = new SignalLifecycleState
                {
                    Direction = signalEvent.Direction,
                    TestNotionalUsd = signalEvent.TestNotionalUsd,
                    StrongestSignalClass = signalEvent.SignalClass,
                    MaxNetEdgeUsd = signalEvent.NetEdgeUsd
                };
                _signalLifecycleStates[key] = lifecycleState;

                await _telegramNotifier.SendMessageAsync(
                    signalEvent.SignalClass == SignalClass.EntryQualified
                        ? $"{AppVersion.ProductName} сигнал открыт: entry-qualified | {_settings.Symbol} | {FormatDirection(signalEvent.Direction)} | {signalEvent.TestNotionalUsd:0.########} USD | net {signalEvent.NetEdgeUsd:+0.########;-0.########;0} USD."
                        : $"{AppVersion.ProductName} сигнал открыт: net-positive | {_settings.Symbol} | {FormatDirection(signalEvent.Direction)} | {signalEvent.TestNotionalUsd:0.########} USD | net {signalEvent.NetEdgeUsd:+0.########;-0.########;0} USD.",
                    cancellationToken);
                return;
            }

            if ((int)signalEvent.SignalClass > (int)lifecycleState.StrongestSignalClass)
            {
                lifecycleState.StrongestSignalClass = signalEvent.SignalClass;
                if (signalEvent.SignalClass == SignalClass.EntryQualified)
                {
                    await _telegramNotifier.SendMessageAsync(
                        $"{AppVersion.ProductName} сигнал усилен до entry-qualified | {_settings.Symbol} | {FormatDirection(signalEvent.Direction)} | {signalEvent.TestNotionalUsd:0.########} USD | net {signalEvent.NetEdgeUsd:+0.########;-0.########;0} USD.",
                        cancellationToken);
                }
            }

            if (_telegramSettings.NotifyOnSignalNewMax && signalEvent.NetEdgeUsd > lifecycleState.MaxNetEdgeUsd)
            {
                lifecycleState.MaxNetEdgeUsd = signalEvent.NetEdgeUsd;
                await _telegramNotifier.SendMessageAsync(
                    $"{AppVersion.ProductName} новый максимум внутри сигнала | {_settings.Symbol} | {FormatDirection(signalEvent.Direction)} | {signalEvent.TestNotionalUsd:0.########} USD | max net {signalEvent.NetEdgeUsd:+0.########;-0.########;0} USD.",
                    cancellationToken);
            }
            else
            {
                lifecycleState.MaxNetEdgeUsd = Math.Max(lifecycleState.MaxNetEdgeUsd, signalEvent.NetEdgeUsd);
            }

            return;
        }

        if (_signalLifecycleStates.Remove(key, out var closedState))
        {
            await _telegramNotifier.SendMessageAsync(
                $"{AppVersion.ProductName} сигнал закрыт | {_settings.Symbol} | {FormatDirection(closedState.Direction)} | {closedState.TestNotionalUsd:0.########} USD | strongest {FormatSignalClass(closedState.StrongestSignalClass)} | max net {closedState.MaxNetEdgeUsd:+0.########;-0.########;0} USD.",
                cancellationToken);
        }
    }

    private static string CreateSignalKey(ArbitrageDirection direction, decimal notionalUsd) =>
        $"{direction}:{notionalUsd:0.########}";

    private static string FormatSignalClass(SignalClass signalClass) =>
        signalClass switch
        {
            SignalClass.EntryQualified => "entry-qualified",
            SignalClass.NetPositive => "net-positive",
            SignalClass.FeePositive => "fee-positive",
            SignalClass.RawPositive => "raw-positive",
            _ => "non-positive"
        };

    private static string FormatDirection(ArbitrageDirection direction) =>
        direction switch
        {
            ArbitrageDirection.BuyBinanceSellBybit => "купить Binance / продать Bybit",
            ArbitrageDirection.BuyBybitSellBinance => "купить Bybit / продать Binance",
            _ => direction.ToString()
        };

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
