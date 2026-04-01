using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Spot;
using Binance.Net.SymbolOrderBooks;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Logging;

namespace ArbiScan.Exchanges.Binance;

public sealed class BinanceSpotExchangeAdapter : IExchangeAdapter, IDisposable
{
    private readonly string _symbol;
    private readonly int _depth;
    private readonly RuntimeMode _runtimeMode;
    private readonly ExchangeConnectionSettings _connectionSettings;
    private readonly ILogger<BinanceSpotExchangeAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private BinanceRestClient? _restClient;
    private BinanceSocketClient? _socketClient;
    private BinanceSpotSymbolOrderBook? _orderBook;

    public BinanceSpotExchangeAdapter(
        string symbol,
        int depth,
        RuntimeMode runtimeMode,
        ExchangeConnectionSettings connectionSettings,
        ILogger<BinanceSpotExchangeAdapter> logger,
        ILoggerFactory loggerFactory)
    {
        _symbol = symbol;
        _depth = depth;
        _runtimeMode = runtimeMode;
        _connectionSettings = connectionSettings;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public ExchangeSymbolRules? Rules { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var environment = _runtimeMode == RuntimeMode.Testnet
            ? BinanceEnvironment.Testnet
            : BinanceEnvironment.Live;

        _restClient = new BinanceRestClient(options =>
        {
            options.Environment = environment;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BinanceCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        _socketClient = new BinanceSocketClient(options =>
        {
            options.Environment = environment;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BinanceCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        var exchangeInfo = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(_symbol, null, cancellationToken);
        if (!exchangeInfo.Success || exchangeInfo.Data is null)
        {
            throw new InvalidOperationException($"Unable to load Binance symbol metadata for {_symbol}: {exchangeInfo.Error}");
        }

        var symbol = exchangeInfo.Data.Symbols.Single();
        Rules = MapRules(symbol);

        _orderBook = new BinanceSpotSymbolOrderBook(
            _symbol,
            options =>
            {
                options.Limit = _depth;
                options.UpdateInterval = 100;
                options.InitialDataTimeout = TimeSpan.FromSeconds(30);
            },
            _loggerFactory,
            _restClient,
            _socketClient);

        _logger.LogInformation("Binance adapter initialized for {Symbol}", _symbol);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_orderBook);
        var result = await _orderBook.StartAsync(cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to start Binance order book for {_symbol}: {result.Error}");
        }

        _logger.LogInformation("Binance order book started for {Symbol}", _symbol);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_orderBook is not null)
        {
            await _orderBook.StopAsync();
        }
    }

    public ExchangeMarketSnapshot? GetSnapshot()
    {
        if (_orderBook is null || Rules is null)
        {
            return null;
        }

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var dataAge = CalculateDataAge(capturedAtUtc, _orderBook.UpdateTime, _orderBook.UpdateServerTime, _orderBook.DataAge);

        return new ExchangeMarketSnapshot(
            ExchangeId.Binance,
            Rules,
            new OrderBookSnapshot(
                _symbol,
                MapStatus(_orderBook.Status),
                capturedAtUtc,
                _orderBook.UpdateTime,
                _orderBook.UpdateServerTime,
                dataAge,
                _orderBook.Bids.Select(x => new OrderBookLevel(x.Price, x.Quantity)).ToArray(),
                _orderBook.Asks.Select(x => new OrderBookLevel(x.Price, x.Quantity)).ToArray()));
    }

    public void Dispose()
    {
        _orderBook?.Dispose();
        _socketClient?.Dispose();
        _restClient?.Dispose();
    }

    private static ExchangeSymbolRules MapRules(BinanceSymbol symbol)
    {
        var minNotional = symbol.NotionalFilter?.MinNotional ??
                          symbol.MinNotionalFilter?.MinNotional ??
                          0m;

        return new ExchangeSymbolRules(
            ExchangeId.Binance,
            symbol.Name,
            symbol.BaseAsset,
            symbol.QuoteAsset,
            symbol.LotSizeFilter?.StepSize ?? symbol.MarketLotSizeFilter?.StepSize ?? 0m,
            symbol.LotSizeFilter?.MinQuantity ?? symbol.MarketLotSizeFilter?.MinQuantity ?? 0m,
            symbol.LotSizeFilter?.MaxQuantity ?? symbol.MarketLotSizeFilter?.MaxQuantity ?? decimal.MaxValue,
            symbol.PriceFilter?.TickSize ?? 0m,
            minNotional);
    }

    private static OrderBookSyncStatus MapStatus(OrderBookStatus status) =>
        status switch
        {
            OrderBookStatus.Disconnected => OrderBookSyncStatus.Disconnected,
            OrderBookStatus.Connecting => OrderBookSyncStatus.Connecting,
            OrderBookStatus.Reconnecting => OrderBookSyncStatus.Reconnecting,
            OrderBookStatus.Syncing => OrderBookSyncStatus.Syncing,
            OrderBookStatus.Synced => OrderBookSyncStatus.Synced,
            OrderBookStatus.Disposing => OrderBookSyncStatus.Disposing,
            OrderBookStatus.Disposed => OrderBookSyncStatus.Disposed,
            _ => OrderBookSyncStatus.Unknown
        };

    private static TimeSpan CalculateDataAge(
        DateTimeOffset capturedAtUtc,
        DateTimeOffset? updateTimeUtc,
        DateTimeOffset? updateServerTimeUtc,
        TimeSpan? fallbackDataAge)
    {
        var updatedAtUtc = updateTimeUtc ?? updateServerTimeUtc;
        if (updatedAtUtc.HasValue)
        {
            var age = capturedAtUtc - updatedAtUtc.Value;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }

        return fallbackDataAge ?? TimeSpan.MaxValue;
    }
}
