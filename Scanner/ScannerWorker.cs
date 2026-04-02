using System.Text.Json;
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

    private readonly Dictionary<ExchangeId, OrderBookSyncStatus> _previousStatuses = new();
    private readonly Dictionary<ExchangeId, int> _dataAgeBuckets = new();
    private readonly Dictionary<ExchangeId, int> _reconnectCountByExchange = new();
    private readonly Dictionary<ExchangeId, int> _resyncCountByExchange = new();
    private readonly Dictionary<ExchangeId, int> _staleCountByExchange = new();
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
                var marketSnapshot = BuildMarketSnapshot(nowUtc);
                _latestMarketSnapshot = marketSnapshot;

                TrackDataAgeDiagnostics(marketSnapshot);
                await DetectHealthTransitionsAsync(marketSnapshot, stoppingToken);

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
                        var closedWindows = _lifetimeTracker.Process(_settings.Symbol, evaluation, _settings);
                        foreach (var window in closedWindows)
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

            if (IsStale(binance, capturedAtUtc, staleThreshold, staleConfirmationWindow))
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

            if (IsStale(bybit, capturedAtUtc, staleThreshold, staleConfirmationWindow))
            {
                flags |= DataHealthFlags.BybitStale;
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

        if (flags != DataHealthFlags.None)
        {
            flags |= DataHealthFlags.Degraded;
        }

        return new MarketDataSnapshot(_settings.Symbol, capturedAtUtc, binance, bybit, flags);
    }

    private bool IsStale(
        ExchangeMarketSnapshot snapshot,
        DateTimeOffset capturedAtUtc,
        TimeSpan staleThreshold,
        TimeSpan staleConfirmationWindow)
    {
        if (snapshot.OrderBook.Status != OrderBookSyncStatus.Synced)
        {
            _quoteStalenessTracker.Reset(snapshot.Exchange);
            return false;
        }

        return _quoteStalenessTracker.IsStale(
            snapshot.Exchange,
            capturedAtUtc,
            snapshot.OrderBook.DataAge,
            staleThreshold,
            staleConfirmationWindow);
    }

    private async Task DetectHealthTransitionsAsync(MarketDataSnapshot snapshot, CancellationToken cancellationToken)
    {
        await TrackExchangeStatusAsync(snapshot.Binance, cancellationToken);
        await TrackExchangeStatusAsync(snapshot.Bybit, cancellationToken);

        var currentHealthy = snapshot.HealthFlags == DataHealthFlags.None;
        if (snapshot.HealthFlags != _previousHealthFlags)
        {
            if (snapshot.HealthFlags.HasFlag(DataHealthFlags.BinanceStale) || snapshot.HealthFlags.HasFlag(DataHealthFlags.BybitStale))
            {
                LogStaleDiagnostics("entered stale", snapshot);
                await PersistHealthEventAsync(
                    new HealthEvent(snapshot.CapturedAtUtc, HealthEventType.StaleQuotesDetected, null, snapshot.HealthFlags, currentHealthy, BuildStaleHealthMessage("detected", snapshot)),
                    cancellationToken);
            }
            else if ((_previousHealthFlags.HasFlag(DataHealthFlags.BinanceStale) || _previousHealthFlags.HasFlag(DataHealthFlags.BybitStale)) && !snapshot.HealthFlags.HasFlag(DataHealthFlags.BinanceStale) && !snapshot.HealthFlags.HasFlag(DataHealthFlags.BybitStale))
            {
                LogStaleDiagnostics("recovered from stale", snapshot);
                await PersistHealthEventAsync(
                    new HealthEvent(snapshot.CapturedAtUtc, HealthEventType.StaleQuotesRecovered, null, snapshot.HealthFlags, currentHealthy, "Stale quotes recovered"),
                    cancellationToken);
            }

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
        var summary = _summaryGenerator.Generate(period, fromUtc, toUtc, windows, healthEvents);
        var healthReport = _healthReportGenerator.Generate(period, fromUtc, toUtc, _settings.Symbol, healthEvents);
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
            var dataAgeMs = exchangeSnapshot.OrderBook.DataAge.TotalMilliseconds;
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
               $"update={exchangeSnapshot.OrderBook.UpdateTimeUtc:O}, serverUpdate={exchangeSnapshot.OrderBook.UpdateServerTimeUtc:O}, " +
               $"callback={exchangeSnapshot.OrderBook.LastUpdateCallbackUtc:O}, " +
               $"levels={exchangeSnapshot.OrderBook.Bids.Count}/{exchangeSnapshot.OrderBook.Asks.Count}, " +
               $"bid={exchangeSnapshot.OrderBook.BestBid?.Price:0.########}/{exchangeSnapshot.OrderBook.BestBid?.Quantity:0.########}, " +
               $"ask={exchangeSnapshot.OrderBook.BestAsk?.Price:0.########}/{exchangeSnapshot.OrderBook.BestAsk?.Quantity:0.########}";
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
