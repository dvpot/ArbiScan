using System.Text.Json;
using System.Threading;
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
    private const double QuietMarketEmergencyThresholdMultiplier = 3d;
    private const int CandidateRejectionSamplesPerHourPerPrimaryReason = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AppSettings _settings;
    private readonly AppStoragePaths _storagePaths;
    private readonly BinanceSpotExchangeAdapter _binanceAdapter;
    private readonly BybitSpotExchangeAdapter _bybitAdapter;
    private readonly IOpportunityDetector _detector;
    private readonly OpportunityLifetimeTracker _lifetimeTracker;
    private readonly IOpportunityRepository _repository;
    private readonly IReportExporter _exporter;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly IHealthReportGenerator _healthReportGenerator;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly TelegramSettings _telegramSettings;
    private readonly ILogger<ScannerWorker> _logger;
    private readonly QuoteStalenessTracker _quoteStalenessTracker = new();
    private readonly RuntimeTelemetryBuffer _runtimeTelemetry = new();

    private readonly Dictionary<ExchangeId, OrderBookSyncStatus> _previousStatuses = new();
    private readonly Dictionary<ExchangeId, int> _dataAgeBuckets = new();
    private readonly Dictionary<ExchangeId, int> _reconnectCountByExchange = new();
    private readonly Dictionary<ExchangeId, int> _resyncCountByExchange = new();
    private readonly Dictionary<ExchangeId, int> _staleCountByExchange = new();
    private readonly Dictionary<string, int> _candidateRejectionSampleCounters = new(StringComparer.Ordinal);
    private DataHealthFlags _previousHealthFlags = DataHealthFlags.None;
    private bool _previousHealthy = true;
    private bool _lastNotifiedHealthy = true;
    private bool? _pendingNotificationHealthyState;
    private DateTimeOffset? _pendingNotificationStateSinceUtc;
    private DateTimeOffset? _lastHealthStateNotificationUtc;
    private DateTimeOffset _startedAtUtc;
    private string _lastStopReason = "Normal shutdown";
    private int _closedWindowCount;
    private MarketDataSnapshot? _latestMarketSnapshot;

    public ScannerWorker(
        AppSettings settings,
        AppStoragePaths storagePaths,
        BinanceSpotExchangeAdapter binanceAdapter,
        BybitSpotExchangeAdapter bybitAdapter,
        IOpportunityDetector detector,
        OpportunityLifetimeTracker lifetimeTracker,
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
        _detector = detector;
        _lifetimeTracker = lifetimeTracker;
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
        var nextSnapshotAtUtc = _startedAtUtc;
        var nextHourlySummaryAtUtc = RoundUpToHour(_startedAtUtc);
        var nextDailySummaryAtUtc = RoundUpToDay(_startedAtUtc);
        var nextCumulativeSummaryAtUtc = _startedAtUtc.AddSeconds(_settings.CumulativeSummaryIntervalSeconds);
        var nextHeartbeatAtUtc = _startedAtUtc.AddMinutes(_telegramSettings.HeartbeatIntervalMinutes);

        try
        {
            await _repository.InitializeAsync(stoppingToken);
            await PersistHealthEventAsync(new HealthEvent(_startedAtUtc, HealthEventType.ApplicationStarted, null, DataHealthFlags.None, true, "Scanner started"), stoppingToken);
            await NotifyStartupAsync(stoppingToken);

            await _binanceAdapter.InitializeAsync(stoppingToken);
            await _bybitAdapter.InitializeAsync(stoppingToken);
            await _binanceAdapter.StartAsync(stoppingToken);
            await _bybitAdapter.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var loopStartedAtUtc = nowUtc;
                var marketSnapshot = BuildMarketSnapshot(nowUtc);
                _latestMarketSnapshot = marketSnapshot;

                TrackDataAgeDiagnostics(marketSnapshot);
                var loopFinishedAtUtc = DateTimeOffset.UtcNow;
                await DetectHealthTransitionsAsync(marketSnapshot, loopStartedAtUtc, loopFinishedAtUtc, stoppingToken);

                if (nowUtc >= nextSnapshotAtUtc)
                {
                    await PersistOrderBookSnapshotsAsync(marketSnapshot, stoppingToken);
                    nextSnapshotAtUtc = nowUtc.AddMilliseconds(_settings.OrderBookSnapshotIntervalMs);
                }

                foreach (var direction in Enum.GetValues<ArbitrageDirection>())
                {
                    foreach (var notional in _settings.TestNotionalsUsd.OrderBy(x => x))
                    {
                        var evaluation = _detector.Evaluate(marketSnapshot, direction, notional, _settings);
                        var trackingResult = _lifetimeTracker.Process(_settings.Symbol, evaluation, _settings);
                        var telemetry = BuildEvaluationTelemetry(marketSnapshot, evaluation, trackingResult.RejectedDueToMinLifetime, _settings);
                        _runtimeTelemetry.RecordEvaluationTelemetry(telemetry);
                        if (telemetry.IsRawPositiveCross && !telemetry.IsProfitable)
                        {
                            var rejectedSignal = BuildRejectedPositiveSignalEvent(marketSnapshot, evaluation, telemetry);
                            _runtimeTelemetry.RecordRejectedPositiveSignal(rejectedSignal);
                            await _exporter.ExportRejectedPositiveSignalAsync(rejectedSignal, stoppingToken);

                            if (ShouldExportCandidateRejectionSample(telemetry))
                            {
                                var candidateRejection = BuildCandidateRejectionEvent(telemetry);
                                await _exporter.ExportCandidateRejectionAsync(candidateRejection, stoppingToken);
                            }
                        }

                        foreach (var window in trackingResult.ClosedWindows)
                        {
                            _closedWindowCount++;
                            await PersistWindowEventAsync(window, stoppingToken);
                        }
                    }
                }

                if (nowUtc >= nextHourlySummaryAtUtc)
                {
                    await GenerateSummaryAsync(SummaryPeriod.Hourly, nextHourlySummaryAtUtc.AddHours(-1), nextHourlySummaryAtUtc, stoppingToken);
                    nextHourlySummaryAtUtc = nextHourlySummaryAtUtc.AddHours(1);
                }

                if (nowUtc >= nextDailySummaryAtUtc)
                {
                    await GenerateSummaryAsync(SummaryPeriod.Daily, nextDailySummaryAtUtc.AddDays(-1), nextDailySummaryAtUtc, stoppingToken);
                    nextDailySummaryAtUtc = nextDailySummaryAtUtc.AddDays(1);
                }

                if (nowUtc >= nextCumulativeSummaryAtUtc)
                {
                    await GenerateSummaryAsync(SummaryPeriod.Cumulative, _startedAtUtc, nowUtc, stoppingToken);
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
            _lastStopReason = "Cancellation requested or host shutdown";
            _logger.LogInformation("Scanner cancellation requested");
        }
        catch (Exception ex)
        {
            _lastStopReason = Shorten(ex.Message, 240);
            await NotifyCriticalErrorAsync(ex, CancellationToken.None);
            throw;
        }
        finally
        {
            await FlushAndShutdownAsync(CancellationToken.None);
        }
    }

    private MarketDataSnapshot BuildMarketSnapshot(DateTimeOffset capturedAtUtc)
    {
        var binance = _binanceAdapter.GetSnapshot();
        var bybit = _bybitAdapter.GetSnapshot();
        var staleThreshold = TimeSpan.FromMilliseconds(_settings.QuoteStalenessThresholdMs);
        var staleConfirmationWindow = TimeSpan.FromMilliseconds(_settings.QuoteStalenessConfirmationMs);

        var flags = DataHealthFlags.None;

        if (binance is null || bybit is null)
        {
            flags |= DataHealthFlags.MissingOrderBook;
        }

        if (binance is not null)
        {
            if (binance.OrderBook.Status != OrderBookSyncStatus.Synced)
            {
                flags |= DataHealthFlags.BinanceOutOfSync;
            }

            if (GetStaleState(binance, capturedAtUtc, staleThreshold, staleConfirmationWindow) == StaleState.Stale)
            {
                flags |= DataHealthFlags.BinanceStale;
            }

            if (binance.OrderBook.Bids.Count == 0 || binance.OrderBook.Asks.Count == 0)
            {
                flags |= DataHealthFlags.InsufficientDepth;
            }
        }

        if (bybit is not null)
        {
            if (bybit.OrderBook.Status != OrderBookSyncStatus.Synced)
            {
                flags |= DataHealthFlags.BybitOutOfSync;
            }

            var bybitStaleState = GetStaleState(bybit, capturedAtUtc, staleThreshold, staleConfirmationWindow);
            if (bybitStaleState == StaleState.Stale)
            {
                flags |= DataHealthFlags.BybitStale;
            }
            else if (bybitStaleState == StaleState.Quiet)
            {
                flags |= DataHealthFlags.BybitQuietMarket;
            }

            if (bybit.OrderBook.Bids.Count == 0 || bybit.OrderBook.Asks.Count == 0)
            {
                flags |= DataHealthFlags.InsufficientDepth;
            }
        }

        if (binance?.Rules is null || bybit?.Rules is null)
        {
            flags |= DataHealthFlags.MissingSymbolRules;
        }

        if (HasHardDegradation(flags))
        {
            flags |= DataHealthFlags.Degraded;
        }

        return new MarketDataSnapshot(_settings.Symbol, capturedAtUtc, binance, bybit, flags);
    }

    private StaleState GetStaleState(
        ExchangeMarketSnapshot snapshot,
        DateTimeOffset capturedAtUtc,
        TimeSpan staleThreshold,
        TimeSpan staleConfirmationWindow)
    {
        if (snapshot.OrderBook.Status != OrderBookSyncStatus.Synced)
        {
            _quoteStalenessTracker.Reset(snapshot.Exchange);
            return StaleState.Fresh;
        }

        var staleConfirmed = _quoteStalenessTracker.IsStale(
            snapshot.Exchange,
            capturedAtUtc,
            snapshot.OrderBook.DataAgeByExchangeTimestamp,
            staleThreshold,
            staleConfirmationWindow);
        if (!staleConfirmed)
        {
            return StaleState.Fresh;
        }

        if (snapshot.Exchange == ExchangeId.Bybit && IsQuietBybitMarket(snapshot, staleThreshold))
        {
            return StaleState.Quiet;
        }

        return StaleState.Stale;
    }

    private async Task DetectHealthTransitionsAsync(
        MarketDataSnapshot snapshot,
        DateTimeOffset loopStartedAtUtc,
        DateTimeOffset loopFinishedAtUtc,
        CancellationToken cancellationToken)
    {
        await TrackExchangeStatusAsync(snapshot.Binance, cancellationToken);
        await TrackExchangeStatusAsync(snapshot.Bybit, cancellationToken);

        var currentHealthy = !HasHardDegradation(snapshot.HealthFlags);
        if (snapshot.HealthFlags != _previousHealthFlags)
        {
            await HandleStaleTransitionsAsync(snapshot, currentHealthy, loopStartedAtUtc, loopFinishedAtUtc, cancellationToken);

            _previousHealthFlags = snapshot.HealthFlags;
        }

        if (currentHealthy != _previousHealthy)
        {
            await PersistHealthEventAsync(
                new HealthEvent(
                    snapshot.CapturedAtUtc,
                    HealthEventType.OverallHealthChanged,
                    null,
                    snapshot.HealthFlags,
                    currentHealthy,
                    currentHealthy ? "Scanner recovered to healthy state" : $"Scanner degraded: {snapshot.HealthFlags}"),
                cancellationToken);

            _previousHealthy = currentHealthy;
        }

        await MaybeNotifyHealthStateAsync(snapshot, currentHealthy, cancellationToken);
    }

    private async Task TrackExchangeStatusAsync(ExchangeMarketSnapshot? snapshot, CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return;
        }

        if (_previousStatuses.TryGetValue(snapshot.Exchange, out var previousStatus) && previousStatus == snapshot.OrderBook.Status)
        {
            return;
        }

        _previousStatuses[snapshot.Exchange] = snapshot.OrderBook.Status;
        await PersistHealthEventAsync(
            new HealthEvent(
                DateTimeOffset.UtcNow,
                HealthEventType.ExchangeStatusChanged,
                snapshot.Exchange,
                snapshot.IsHealthy(TimeSpan.FromMilliseconds(_settings.QuoteStalenessThresholdMs)) ? DataHealthFlags.None : DataHealthFlags.Degraded,
                snapshot.OrderBook.Status == OrderBookSyncStatus.Synced,
                $"{snapshot.Exchange} order book status changed to {snapshot.OrderBook.Status}. {DescribeExchange(snapshot)}"),
            cancellationToken);

        if (snapshot.OrderBook.Status == OrderBookSyncStatus.Reconnecting)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ReconnectStarted, snapshot.Exchange, DataHealthFlags.Degraded, false, $"{snapshot.Exchange} reconnect started"), cancellationToken);
        }

        if (snapshot.OrderBook.Status == OrderBookSyncStatus.Syncing)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ResyncStarted, snapshot.Exchange, DataHealthFlags.Degraded, false, $"{snapshot.Exchange} resync started"), cancellationToken);
        }

        if (snapshot.OrderBook.Status == OrderBookSyncStatus.Synced)
        {
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ReconnectCompleted, snapshot.Exchange, DataHealthFlags.None, true, $"{snapshot.Exchange} reconnected"), cancellationToken);
            await PersistHealthEventAsync(new HealthEvent(DateTimeOffset.UtcNow, HealthEventType.ResyncCompleted, snapshot.Exchange, DataHealthFlags.None, true, $"{snapshot.Exchange} resynced"), cancellationToken);
        }
    }

    private async Task PersistOrderBookSnapshotsAsync(MarketDataSnapshot marketSnapshot, CancellationToken cancellationToken)
    {
        foreach (var snapshot in new[] { marketSnapshot.Binance, marketSnapshot.Bybit })
        {
            if (snapshot is null)
            {
                continue;
            }

            var record = new OrderBookSnapshotRecord(
                marketSnapshot.CapturedAtUtc,
                snapshot.Exchange,
                snapshot.OrderBook.Symbol,
                snapshot.OrderBook.Status,
                snapshot.OrderBook.DataAge,
                snapshot.OrderBook.BestBid?.Price,
                snapshot.OrderBook.BestBid?.Quantity,
                snapshot.OrderBook.BestAsk?.Price,
                snapshot.OrderBook.BestAsk?.Quantity,
                JsonSerializer.Serialize(snapshot, JsonOptions));

            await _repository.SaveOrderBookSnapshotAsync(record, cancellationToken);
            await _exporter.ExportOrderBookSnapshotAsync(record, cancellationToken);
        }
    }

    private async Task PersistWindowEventAsync(OpportunityWindowEvent windowEvent, CancellationToken cancellationToken)
    {
        await _repository.SaveWindowEventAsync(windowEvent, cancellationToken);
        await _exporter.ExportWindowEventAsync(windowEvent, cancellationToken);
    }

    private async Task PersistHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken)
    {
        UpdateHealthCounters(healthEvent);
        _logger.LogInformation("Health event: {EventType} {Exchange} {Message}", healthEvent.EventType, healthEvent.Exchange, healthEvent.Message);
        await _repository.SaveHealthEventAsync(healthEvent, cancellationToken);
        await _exporter.ExportHealthEventAsync(healthEvent, cancellationToken);
    }

    private async Task GenerateSummaryAsync(SummaryPeriod period, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var windows = await _repository.GetWindowEventsAsync(fromUtc, toUtc, cancellationToken);
        var healthEvents = await _repository.GetHealthEventsAsync(fromUtc, toUtc, cancellationToken);
        var evaluationTelemetry = _runtimeTelemetry.GetEvaluationTelemetry(fromUtc, toUtc);
        var staleDiagnostics = _runtimeTelemetry.GetStaleDiagnostics(fromUtc, toUtc);
        var summary = _summaryGenerator.Generate(period, fromUtc, toUtc, _settings.Symbol, windows, healthEvents, evaluationTelemetry);
        var healthReport = _healthReportGenerator.Generate(period, fromUtc, toUtc, _settings.Symbol, healthEvents, staleDiagnostics);
        await _repository.SaveSummaryAsync(summary, cancellationToken);
        await _exporter.ExportSummaryAsync(summary, cancellationToken);
        await _exporter.ExportHealthReportAsync(healthReport, cancellationToken);
        _logger.LogInformation("Generated {Period} summary for {From}..{To}. Total windows: {Count}", period, fromUtc, toUtc, summary.TotalWindows);
    }

    private async Task FlushAndShutdownAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var window in _lifetimeTracker.Flush(_settings.Symbol, nowUtc, _settings))
        {
            _closedWindowCount++;
            await PersistWindowEventAsync(window, cancellationToken);
        }

        await GenerateSummaryAsync(SummaryPeriod.Cumulative, _startedAtUtc, nowUtc, cancellationToken);
        await PersistHealthEventAsync(new HealthEvent(nowUtc, HealthEventType.ApplicationStopping, null, DataHealthFlags.None, true, "Scanner stopping"), cancellationToken);
        await NotifyShutdownAsync(cancellationToken);

        await _binanceAdapter.StopAsync(cancellationToken);
        await _bybitAdapter.StopAsync(cancellationToken);
        _binanceAdapter.Dispose();
        _bybitAdapter.Dispose();
    }

    private static DateTimeOffset RoundUpToHour(DateTimeOffset timestampUtc)
    {
        var truncated = new DateTimeOffset(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, timestampUtc.Hour, 0, 0, TimeSpan.Zero);
        return truncated <= timestampUtc ? truncated.AddHours(1) : truncated;
    }

    private static DateTimeOffset RoundUpToDay(DateTimeOffset timestampUtc)
    {
        var truncated = new DateTimeOffset(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, 0, 0, 0, TimeSpan.Zero);
        return truncated <= timestampUtc ? truncated.AddDays(1) : truncated;
    }

    private async Task NotifyStartupAsync(CancellationToken cancellationToken)
    {
        if (!_telegramNotifier.IsEnabled || !_telegramSettings.NotifyOnStartup)
        {
            return;
        }

        await _telegramNotifier.SendMessageAsync(
            $"ArbiScan v{AppVersion.Current} started\nSymbol: {_settings.Symbol}\nMode: {_settings.RuntimeMode}\nStatus: starting",
            cancellationToken);
    }

    private async Task NotifyShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_telegramNotifier.IsEnabled || !_telegramSettings.NotifyOnShutdown)
        {
            return;
        }

        await _telegramNotifier.SendMessageAsync(
            $"ArbiScan v{AppVersion.Current} stopped\nSymbol: {_settings.Symbol}\nReason: {_lastStopReason}\nClosed windows: {_closedWindowCount}",
            cancellationToken);
    }

    private async Task NotifyCriticalErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (!_telegramNotifier.IsEnabled || !_telegramSettings.NotifyOnCriticalError)
        {
            return;
        }

        await _telegramNotifier.SendMessageAsync(
            $"ArbiScan v{AppVersion.Current} critical error\nSymbol: {_settings.Symbol}\nType: {exception.GetType().Name}\nMessage: {Shorten(exception.Message, 500)}",
            cancellationToken);
    }

    private string BuildHeartbeatMessage(MarketDataSnapshot snapshot)
    {
        return
            $"ArbiScan v{AppVersion.Current} heartbeat\n" +
            $"Symbol: {_settings.Symbol}\n" +
            $"Health: {(snapshot.HealthFlags == DataHealthFlags.None ? "healthy" : snapshot.HealthFlags)}\n" +
            $"Binance: {DescribeExchange(snapshot.Binance)}\n" +
            $"Bybit: {DescribeExchange(snapshot.Bybit)}\n" +
            $"Closed windows: {_closedWindowCount}\n" +
            $"Stale events: {FormatCounters(_staleCountByExchange)}\n" +
            $"Reconnects: {FormatCounters(_reconnectCountByExchange)}\n" +
            $"Resyncs: {FormatCounters(_resyncCountByExchange)}\n" +
            $"Uptime: {Math.Round((DateTimeOffset.UtcNow - _startedAtUtc).TotalMinutes)} min";
    }

    private async Task MaybeNotifyHealthStateAsync(MarketDataSnapshot snapshot, bool currentHealthy, CancellationToken cancellationToken)
    {
        if (!_telegramNotifier.IsEnabled || !_telegramSettings.NotifyOnHealthStateChanges)
        {
            return;
        }

        if (currentHealthy == _lastNotifiedHealthy)
        {
            _pendingNotificationHealthyState = null;
            _pendingNotificationStateSinceUtc = null;
            return;
        }

        if (_pendingNotificationHealthyState != currentHealthy)
        {
            _pendingNotificationHealthyState = currentHealthy;
            _pendingNotificationStateSinceUtc = snapshot.CapturedAtUtc;
            return;
        }

        var requiredStableDuration = TimeSpan.FromMilliseconds(
            currentHealthy
                ? _telegramSettings.RequireStableHealthyBeforeNotifyMs
                : _telegramSettings.RequireStableDegradedBeforeNotifyMs);

        if (_pendingNotificationStateSinceUtc.HasValue &&
            snapshot.CapturedAtUtc - _pendingNotificationStateSinceUtc.Value < requiredStableDuration)
        {
            return;
        }

        var minNotifyInterval = TimeSpan.FromSeconds(_telegramSettings.HealthStateChangeMinNotifyIntervalSeconds);
        if (_lastHealthStateNotificationUtc.HasValue &&
            snapshot.CapturedAtUtc - _lastHealthStateNotificationUtc.Value < minNotifyInterval)
        {
            return;
        }

        await _telegramNotifier.SendMessageAsync(BuildHealthStateChangeMessage(snapshot, currentHealthy), cancellationToken);
        _lastHealthStateNotificationUtc = snapshot.CapturedAtUtc;
        _lastNotifiedHealthy = currentHealthy;
        _pendingNotificationHealthyState = null;
        _pendingNotificationStateSinceUtc = null;
    }

    private string BuildHealthStateChangeMessage(MarketDataSnapshot snapshot, bool currentHealthy) =>
        currentHealthy
            ? $"ArbiScan v{AppVersion.Current} status: WORKING\n" +
              $"Symbol: {_settings.Symbol}\n" +
              $"Health: healthy\n" +
              $"Binance: {DescribeExchange(snapshot.Binance)}\n" +
              $"Bybit: {DescribeExchange(snapshot.Bybit)}"
            : $"ArbiScan v{AppVersion.Current} status: DEGRADED\n" +
              $"Symbol: {_settings.Symbol}\n" +
              $"Flags: {snapshot.HealthFlags}\n" +
              $"Binance: {DescribeExchange(snapshot.Binance)}\n" +
              $"Bybit: {DescribeExchange(snapshot.Bybit)}\n" +
              $"Reconnects: {FormatCounters(_reconnectCountByExchange)}\n" +
              $"Resyncs: {FormatCounters(_resyncCountByExchange)}\n" +
              $"Stale events: {FormatCounters(_staleCountByExchange)}";

    private void TrackDataAgeDiagnostics(MarketDataSnapshot snapshot)
    {
        foreach (var exchangeSnapshot in new[] { snapshot.Binance, snapshot.Bybit })
        {
            if (exchangeSnapshot is null || exchangeSnapshot.OrderBook.Status != OrderBookSyncStatus.Synced)
            {
                if (exchangeSnapshot is not null)
                {
                    _dataAgeBuckets.Remove(exchangeSnapshot.Exchange);
                }

                continue;
            }

            var threshold = _settings.QuoteStalenessThresholdMs;
            var dataAgeMs = exchangeSnapshot.OrderBook.DataAgeByExchangeTimestamp.TotalMilliseconds;
            var bucket = dataAgeMs >= threshold ? 3 : dataAgeMs >= 2_000 ? 2 : dataAgeMs >= 1_000 ? 1 : 0;
            _dataAgeBuckets.TryGetValue(exchangeSnapshot.Exchange, out var previousBucket);

            if (bucket > previousBucket)
            {
                _logger.LogWarning(
                    "{Exchange} data age threshold crossed: bucket={Bucket}, ageMs={AgeMs}. {Details}",
                    exchangeSnapshot.Exchange,
                    bucket,
                    Math.Round(dataAgeMs),
                    DescribeExchange(exchangeSnapshot));
            }

            if (bucket == 0)
            {
                _dataAgeBuckets.Remove(exchangeSnapshot.Exchange);
            }
            else
            {
                _dataAgeBuckets[exchangeSnapshot.Exchange] = bucket;
            }
        }
    }

    private void LogStaleDiagnostics(string transition, MarketDataSnapshot snapshot)
    {
        _logger.LogWarning(
            "Stale transition {Transition}. Binance=[{Binance}] Bybit=[{Bybit}]",
            transition,
            DescribeExchange(snapshot.Binance),
            DescribeExchange(snapshot.Bybit));
    }

    private string BuildStaleHealthMessage(string verb, MarketDataSnapshot snapshot) =>
        $"Stale quotes {verb}: {snapshot.HealthFlags}. Binance=[{DescribeExchange(snapshot.Binance)}] Bybit=[{DescribeExchange(snapshot.Bybit)}]";

    private void UpdateHealthCounters(HealthEvent healthEvent)
    {
        if (healthEvent.Exchange is { } exchange)
        {
            switch (healthEvent.EventType)
            {
                case HealthEventType.ReconnectStarted:
                    Increment(_reconnectCountByExchange, exchange);
                    break;
                case HealthEventType.ResyncStarted:
                    Increment(_resyncCountByExchange, exchange);
                    break;
            }
        }

        if (healthEvent.EventType == HealthEventType.StaleQuotesDetected)
        {
            if (healthEvent.Flags.HasFlag(DataHealthFlags.BinanceStale))
            {
                Increment(_staleCountByExchange, ExchangeId.Binance);
            }

            if (healthEvent.Flags.HasFlag(DataHealthFlags.BybitStale))
            {
                Increment(_staleCountByExchange, ExchangeId.Bybit);
            }
        }
    }

    private static void Increment(Dictionary<ExchangeId, int> counters, ExchangeId exchange) =>
        counters[exchange] = counters.GetValueOrDefault(exchange) + 1;

    private static string FormatCounters(Dictionary<ExchangeId, int> counters) =>
        $"Binance={counters.GetValueOrDefault(ExchangeId.Binance)}, Bybit={counters.GetValueOrDefault(ExchangeId.Bybit)}";

    private static string DescribeExchange(ExchangeMarketSnapshot? exchangeSnapshot)
    {
        if (exchangeSnapshot is null)
        {
            return "n/a";
        }

        return $"{exchangeSnapshot.OrderBook.Status}, age={Math.Round(exchangeSnapshot.OrderBook.DataAge.TotalMilliseconds)}ms, " +
               $"exchangeAge={Math.Round(exchangeSnapshot.OrderBook.DataAgeByExchangeTimestamp.TotalMilliseconds)}ms, " +
               $"callbackAge={Math.Round(exchangeSnapshot.OrderBook.DataAgeByLocalCallbackTimestamp.TotalMilliseconds)}ms, " +
               $"topAge={Math.Round(exchangeSnapshot.OrderBook.TimeSinceTopOfBookChanged.TotalMilliseconds)}ms, " +
               $"update={exchangeSnapshot.OrderBook.UpdateTimeUtc:O}, serverUpdate={exchangeSnapshot.OrderBook.UpdateServerTimeUtc:O}, " +
               $"callback={exchangeSnapshot.OrderBook.LastUpdateCallbackUtc:O}, " +
               $"levels={exchangeSnapshot.OrderBook.Bids.Count}/{exchangeSnapshot.OrderBook.Asks.Count}, " +
               $"bid={exchangeSnapshot.OrderBook.BestBid?.Price:0.########}/{exchangeSnapshot.OrderBook.BestBid?.Quantity:0.########}, " +
               $"ask={exchangeSnapshot.OrderBook.BestAsk?.Price:0.########}/{exchangeSnapshot.OrderBook.BestAsk?.Quantity:0.########}";
    }

    private async Task HandleStaleTransitionsAsync(
        MarketDataSnapshot snapshot,
        bool currentHealthy,
        DateTimeOffset loopStartedAtUtc,
        DateTimeOffset loopFinishedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var exchange in new[] { ExchangeId.Binance, ExchangeId.Bybit })
        {
            var previousStale = HasStaleFlag(_previousHealthFlags, exchange);
            var currentStale = HasStaleFlag(snapshot.HealthFlags, exchange);
            if (previousStale == currentStale)
            {
                continue;
            }

            var exchangeSnapshot = snapshot.GetExchange(exchange);
            if (exchangeSnapshot is null)
            {
                continue;
            }

            var eventType = currentStale ? "stale_detected" : "stale_recovered";
            var diagnosticEvent = BuildStaleDiagnosticEvent(snapshot, exchangeSnapshot, eventType, loopStartedAtUtc, loopFinishedAtUtc);
            _runtimeTelemetry.RecordStaleDiagnostic(diagnosticEvent);
            await _exporter.ExportStaleDiagnosticAsync(diagnosticEvent, cancellationToken);

            LogStaleDiagnostics(currentStale ? "entered stale" : "recovered from stale", snapshot);
            await PersistHealthEventAsync(
                new HealthEvent(
                    snapshot.CapturedAtUtc,
                    currentStale ? HealthEventType.StaleQuotesDetected : HealthEventType.StaleQuotesRecovered,
                    exchange,
                    BuildExchangeSpecificStaleFlags(exchange, snapshot.HealthFlags, currentStale),
                    currentHealthy,
                    currentStale ? BuildStaleHealthMessage("detected", snapshot) : $"Stale quotes recovered: {exchange}. {DescribeExchange(exchangeSnapshot)}"),
                cancellationToken);
        }
    }

    private EvaluationTelemetrySnapshot BuildEvaluationTelemetry(
        MarketDataSnapshot snapshot,
        OpportunityPairEvaluation evaluation,
        bool rejectedDueToMinLifetime,
        AppSettings settings)
    {
        var rawPositiveCross = HasRawPositiveCross(snapshot, evaluation.Direction);
        var conservative = evaluation.Conservative;
        var profitable = conservative.IsProfitable(
            settings.Thresholds.EntryThresholdUsd,
            settings.Thresholds.EntryThresholdBps);
        var rejectReasons = rawPositiveCross
            ? DetermineRejectReasons(snapshot, evaluation, profitable, rejectedDueToMinLifetime)
            : [];
        var buyCostUsd = conservative.BuyLeg?.GrossQuoteAmountUsd ?? 0m;
        var netBeforeFeesUsd = conservative.GrossPnlUsd - conservative.BuffersTotalUsd;
        var netEdgeBeforeFeesPct = buyCostUsd > 0m ? netBeforeFeesUsd / buyCostUsd : 0m;
        var rejectAnalysis = AnalyzeRejectReasons(snapshot, evaluation, rawPositiveCross, profitable, rejectedDueToMinLifetime, settings, buyCostUsd);

        return new EvaluationTelemetrySnapshot(
            evaluation.TimestampUtc,
            snapshot.Symbol,
            evaluation.Direction,
            evaluation.TestNotionalUsd,
            rawPositiveCross,
            profitable,
            !HasHardDegradation(snapshot.HealthFlags),
            conservative.FillabilityStatus,
            conservative.GrossPnlUsd,
            conservative.GrossEdgePct,
            netEdgeBeforeFeesPct,
            conservative.FeesTotalUsd,
            conservative.BuffersTotalUsd,
            conservative.NetPnlUsd,
            conservative.NetEdgePct,
            conservative.FillableBaseQuantity,
            snapshot.HealthFlags,
            rejectAnalysis.RejectReasons,
            rejectAnalysis.PrimaryRejectReason,
            rejectAnalysis.SecondaryRejectReasons,
            rejectAnalysis.WouldBeProfitableWithoutFees,
            rejectAnalysis.WouldBeProfitableWithoutFillability,
            evaluation.BinanceBestBid,
            evaluation.BinanceBestAsk,
            evaluation.BybitBestBid,
            evaluation.BybitBestAsk,
            evaluation.FillabilityDecision);
    }

    private bool HasRawPositiveCross(MarketDataSnapshot snapshot, ArbitrageDirection direction)
    {
        var buySnapshot = snapshot.GetExchange(direction.BuyExchange());
        var sellSnapshot = snapshot.GetExchange(direction.SellExchange());
        var buyAsk = buySnapshot?.OrderBook.BestAsk?.Price;
        var sellBid = sellSnapshot?.OrderBook.BestBid?.Price;
        return buyAsk.HasValue && sellBid.HasValue && sellBid.Value > buyAsk.Value;
    }

    private IReadOnlyList<string> DetermineRejectReasons(
        MarketDataSnapshot snapshot,
        OpportunityPairEvaluation evaluation,
        bool profitable,
        bool rejectedDueToMinLifetime)
    {
        if (profitable)
        {
            return rejectedDueToMinLifetime ? ["min_lifetime"] : [];
        }

        var conservative = evaluation.Conservative;
        var reasons = new List<string>();

        if (HasHardDegradation(snapshot.HealthFlags))
        {
            reasons.Add("health");
        }

        if (string.Equals(conservative.RejectReason, "Symbol minimums not met", StringComparison.Ordinal))
        {
            reasons.Add("rules");
        }

        if (conservative.FillabilityStatus != FillabilityStatus.Fillable ||
            conservative.RejectReason is "Executable quantity rounded to zero" or "Insufficient depth for executable quantity" or "Missing market snapshot")
        {
            reasons.Add("fillability");
        }

        if (conservative.GrossPnlUsd > 0m && conservative.GrossPnlUsd - conservative.FeesTotalUsd <= 0m)
        {
            reasons.Add("fees");
        }

        if (conservative.GrossPnlUsd - conservative.FeesTotalUsd > 0m && conservative.NetPnlUsd <= 0m)
        {
            reasons.Add("buffers");
        }

        if (rejectedDueToMinLifetime)
        {
            reasons.Add("min_lifetime");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("other");
        }

        return reasons
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private RejectedPositiveSignalEvent BuildRejectedPositiveSignalEvent(
        MarketDataSnapshot snapshot,
        OpportunityPairEvaluation evaluation,
        EvaluationTelemetrySnapshot telemetry) =>
        new(
            telemetry.TimestampUtc,
            snapshot.Symbol,
            telemetry.Direction,
            telemetry.TestNotionalUsd,
            telemetry.BinanceBestBid,
            telemetry.BinanceBestAsk,
            telemetry.BybitBestBid,
            telemetry.BybitBestAsk,
            telemetry.GrossEdgePct,
            telemetry.NetEdgeBeforeFeesPct,
            telemetry.FeesTotalUsd,
            telemetry.BuffersTotalUsd,
            telemetry.FillabilityStatus,
            telemetry.FillableBaseQuantity,
            telemetry.RejectReasons,
            telemetry.HealthFlags,
            telemetry.IsSnapshotUsableForEvaluation);

    private CandidateRejectionEvent BuildCandidateRejectionEvent(EvaluationTelemetrySnapshot telemetry)
    {
        var buyExchange = telemetry.Direction.BuyExchange().ToString();
        var sellExchange = telemetry.Direction.SellExchange().ToString();
        var bestAsk = telemetry.Direction.BuyExchange() == ExchangeId.Binance ? telemetry.BinanceBestAsk : telemetry.BybitBestAsk;
        var bestBid = telemetry.Direction.SellExchange() == ExchangeId.Binance ? telemetry.BinanceBestBid : telemetry.BybitBestBid;

        return new CandidateRejectionEvent(
            telemetry.TimestampUtc,
            telemetry.Symbol,
            telemetry.Direction,
            telemetry.TestNotionalUsd,
            buyExchange,
            sellExchange,
            bestAsk,
            bestBid,
            telemetry.FillabilityDecision.BuyTopOfBookQuantity,
            telemetry.FillabilityDecision.SellTopOfBookQuantity,
            telemetry.FillabilityDecision.BuyAggregatedFillableQuantity,
            telemetry.FillabilityDecision.SellAggregatedFillableQuantity,
            telemetry.FillabilityDecision.QuantityBeforeRounding,
            telemetry.FillabilityDecision.QuantityAfterRoundingBinanceRules,
            telemetry.FillabilityDecision.QuantityAfterRoundingBybitRules,
            telemetry.FillabilityDecision.EffectiveExecutableQuantity,
            telemetry.GrossPnlUsd,
            telemetry.GrossEdgePct * 10_000m,
            telemetry.FeesTotalUsd,
            telemetry.BuffersTotalUsd,
            telemetry.NetEdgeUsd,
            telemetry.NetEdgePct * 10_000m,
            telemetry.RejectReasons,
            telemetry.PrimaryRejectReason,
            telemetry.SecondaryRejectReasons,
            telemetry.FillabilityDecision);
    }

    private bool ShouldExportCandidateRejectionSample(EvaluationTelemetrySnapshot telemetry)
    {
        var bucket = telemetry.TimestampUtc.UtcDateTime.ToString("yyyyMMddHH");
        var primaryReason = telemetry.PrimaryRejectReason ?? "none";
        var key = $"{bucket}:{primaryReason}";

        if (_candidateRejectionSampleCounters.TryGetValue(key, out var currentCount) &&
            currentCount >= CandidateRejectionSamplesPerHourPerPrimaryReason)
        {
            return false;
        }

        _candidateRejectionSampleCounters[key] = currentCount + 1;
        return true;
    }

    private RejectAnalysis AnalyzeRejectReasons(
        MarketDataSnapshot snapshot,
        OpportunityPairEvaluation evaluation,
        bool rawPositiveCross,
        bool profitable,
        bool rejectedDueToMinLifetime,
        AppSettings settings,
        decimal buyCostUsd)
    {
        var rejectReasons = rawPositiveCross
            ? DetermineRejectReasons(snapshot, evaluation, profitable, rejectedDueToMinLifetime)
            : [];

        var primaryRejectReason = rejectReasons.Count > 0 ? rejectReasons[0] : null;
        var secondaryRejectReasons = rejectReasons.Skip(1).ToArray();
        var conservative = evaluation.Conservative;
        var wouldBeProfitableWithoutFees = WouldBeProfitableWithoutFees(conservative, snapshot, rejectedDueToMinLifetime, settings, buyCostUsd);
        var wouldBeProfitableWithoutFillability = WouldBeProfitableWithoutFillability(conservative, snapshot, rejectedDueToMinLifetime, settings);

        return new RejectAnalysis(
            rejectReasons,
            primaryRejectReason,
            secondaryRejectReasons,
            wouldBeProfitableWithoutFees,
            wouldBeProfitableWithoutFillability);
    }

    private static bool WouldBeProfitableWithoutFees(
        OpportunityEvaluation conservative,
        MarketDataSnapshot snapshot,
        bool rejectedDueToMinLifetime,
        AppSettings settings,
        decimal buyCostUsd)
    {
        if (HasHardDegradation(snapshot.HealthFlags) || rejectedDueToMinLifetime)
        {
            return false;
        }

        if (string.Equals(conservative.RejectReason, "Symbol minimums not met", StringComparison.Ordinal) ||
            conservative.FillabilityStatus != FillabilityStatus.Fillable)
        {
            return false;
        }

        var netWithoutFeesUsd = conservative.NetPnlUsd + conservative.FeesTotalUsd;
        var netWithoutFeesBps = buyCostUsd > 0m ? (netWithoutFeesUsd / buyCostUsd) * 10_000m : 0m;
        return netWithoutFeesUsd > 0m &&
               netWithoutFeesUsd >= settings.Thresholds.EntryThresholdUsd &&
               netWithoutFeesBps >= settings.Thresholds.EntryThresholdBps;
    }

    private static bool WouldBeProfitableWithoutFillability(
        OpportunityEvaluation conservative,
        MarketDataSnapshot snapshot,
        bool rejectedDueToMinLifetime,
        AppSettings settings)
    {
        if (HasHardDegradation(snapshot.HealthFlags) || rejectedDueToMinLifetime)
        {
            return false;
        }

        if (string.Equals(conservative.RejectReason, "Symbol minimums not met", StringComparison.Ordinal))
        {
            return false;
        }

        return conservative.NetPnlUsd > 0m &&
               conservative.NetPnlUsd >= settings.Thresholds.EntryThresholdUsd &&
               conservative.NetEdgePct * 10_000m >= settings.Thresholds.EntryThresholdBps;
    }

    private sealed record RejectAnalysis(
        IReadOnlyList<string> RejectReasons,
        string? PrimaryRejectReason,
        IReadOnlyList<string> SecondaryRejectReasons,
        bool WouldBeProfitableWithoutFees,
        bool WouldBeProfitableWithoutFillability);

    private StaleDiagnosticEvent BuildStaleDiagnosticEvent(
        MarketDataSnapshot snapshot,
        ExchangeMarketSnapshot exchangeSnapshot,
        string eventType,
        DateTimeOffset loopStartedAtUtc,
        DateTimeOffset loopFinishedAtUtc)
    {
        var degradedFlags = ExpandFlags(snapshot.HealthFlags).ToArray();

        return new StaleDiagnosticEvent(
            snapshot.CapturedAtUtc,
            exchangeSnapshot.Exchange,
            eventType,
            snapshot.Symbol,
            _settings.QuoteStalenessThresholdMs,
            _settings.QuoteStalenessConfirmationMs,
            _settings.ScanIntervalMs,
            exchangeSnapshot.OrderBook.Status,
            snapshot.CapturedAtUtc,
            exchangeSnapshot.OrderBook.UpdateTimeUtc,
            exchangeSnapshot.OrderBook.UpdateServerTimeUtc,
            exchangeSnapshot.OrderBook.LastUpdateCallbackUtc,
            exchangeSnapshot.OrderBook.DataAge.TotalMilliseconds,
            exchangeSnapshot.OrderBook.DataAgeByExchangeTimestamp.TotalMilliseconds,
            exchangeSnapshot.OrderBook.DataAgeByLocalCallbackTimestamp.TotalMilliseconds,
            exchangeSnapshot.OrderBook.DataAgeByLocalCallbackTimestamp.TotalMilliseconds,
            exchangeSnapshot.OrderBook.TimeSinceTopOfBookChanged.TotalMilliseconds,
            exchangeSnapshot.OrderBook.BestBid?.Price,
            exchangeSnapshot.OrderBook.BestAsk?.Price,
            exchangeSnapshot.OrderBook.Bids.Count,
            exchangeSnapshot.OrderBook.Asks.Count,
            exchangeSnapshot.OrderBook.BestBid?.Quantity,
            exchangeSnapshot.OrderBook.BestAsk?.Quantity,
            loopStartedAtUtc,
            loopFinishedAtUtc,
            (loopFinishedAtUtc - loopStartedAtUtc).TotalMilliseconds,
            Environment.CurrentManagedThreadId,
            !HasHardDegradation(snapshot.HealthFlags),
            degradedFlags,
            DetermineStaleLikelyRootCause(exchangeSnapshot));
    }

    private static bool HasStaleFlag(DataHealthFlags flags, ExchangeId exchange) =>
        exchange switch
        {
            ExchangeId.Binance => flags.HasFlag(DataHealthFlags.BinanceStale),
            ExchangeId.Bybit => flags.HasFlag(DataHealthFlags.BybitStale),
            _ => false
        };

    private static bool HasHardDegradation(DataHealthFlags flags)
    {
        var hardFlags = flags & ~DataHealthFlags.BybitQuietMarket;
        return hardFlags != DataHealthFlags.None;
    }

    private static DataHealthFlags BuildExchangeSpecificStaleFlags(ExchangeId exchange, DataHealthFlags snapshotFlags, bool currentStale)
    {
        var exchangeFlag = exchange switch
        {
            ExchangeId.Binance => DataHealthFlags.BinanceStale,
            ExchangeId.Bybit => DataHealthFlags.BybitStale,
            _ => DataHealthFlags.None
        };

        var baseFlags = snapshotFlags & ~(DataHealthFlags.BinanceStale | DataHealthFlags.BybitStale);
        if (!currentStale)
        {
            return baseFlags;
        }

        return baseFlags | exchangeFlag | DataHealthFlags.Degraded;
    }

    private bool IsQuietBybitMarket(ExchangeMarketSnapshot snapshot, TimeSpan staleThreshold)
    {
        var callbackSilenceMs = snapshot.OrderBook.DataAgeByLocalCallbackTimestamp.TotalMilliseconds;
        var exchangeAgeMs = snapshot.OrderBook.DataAgeByExchangeTimestamp.TotalMilliseconds;
        var topChangeAgeMs = snapshot.OrderBook.TimeSinceTopOfBookChanged.TotalMilliseconds;
        var thresholdMs = staleThreshold.TotalMilliseconds;
        var emergencyThresholdMs = thresholdMs * QuietMarketEmergencyThresholdMultiplier;
        var agesAlmostEqual = Math.Abs(callbackSilenceMs - exchangeAgeMs) <= 250d;

        return snapshot.OrderBook.Status == OrderBookSyncStatus.Synced &&
               topChangeAgeMs >= thresholdMs &&
               agesAlmostEqual &&
               exchangeAgeMs < emergencyThresholdMs &&
               callbackSilenceMs < emergencyThresholdMs;
    }

    private static IEnumerable<string> ExpandFlags(DataHealthFlags flags)
    {
        foreach (var value in Enum.GetValues<DataHealthFlags>())
        {
            if (value == DataHealthFlags.None)
            {
                continue;
            }

            if (flags.HasFlag(value))
            {
                yield return value.ToString();
            }
        }
    }

    private string DetermineStaleLikelyRootCause(ExchangeMarketSnapshot exchangeSnapshot)
    {
        var callbackSilenceMs = exchangeSnapshot.OrderBook.DataAgeByLocalCallbackTimestamp.TotalMilliseconds;
        var exchangeAgeMs = exchangeSnapshot.OrderBook.DataAgeByExchangeTimestamp.TotalMilliseconds;
        var topChangeAgeMs = exchangeSnapshot.OrderBook.TimeSinceTopOfBookChanged.TotalMilliseconds;
        var thresholdMs = _settings.QuoteStalenessThresholdMs;

        if (callbackSilenceMs >= thresholdMs && callbackSilenceMs >= exchangeAgeMs * 1.25d)
        {
            return "callback_gap";
        }

        if (exchangeAgeMs >= thresholdMs && callbackSilenceMs < thresholdMs)
        {
            return "exchange_timestamp_gap";
        }

        if (topChangeAgeMs >= thresholdMs && callbackSilenceMs < thresholdMs * 1.25d)
        {
            return "top_of_book_unchanged";
        }

        if (exchangeSnapshot.Exchange == ExchangeId.Bybit && IsQuietBybitMarket(exchangeSnapshot, TimeSpan.FromMilliseconds(_settings.QuoteStalenessThresholdMs)))
        {
            return "callback_gap_without_book_change";
        }

        return "unknown";
    }

    private enum StaleState
    {
        Fresh = 0,
        Quiet = 1,
        Stale = 2
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
