using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using Bybit.Net.SymbolOrderBooks;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Logging;

namespace ArbiScan.Exchanges.Bybit;

public sealed class BybitSpotExchangeAdapter : IExchangeAdapter, IDisposable
{
    private readonly string _symbol;
    private readonly int _depth;
    private readonly RuntimeMode _runtimeMode;
    private readonly ExchangeConnectionSettings _connectionSettings;
    private readonly ILogger<BybitSpotExchangeAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private BybitRestClient? _restClient;
    private BybitSocketClient? _socketClient;
    private BybitSymbolOrderBook? _orderBook;

    public BybitSpotExchangeAdapter(
        string symbol,
        int depth,
        RuntimeMode runtimeMode,
        ExchangeConnectionSettings connectionSettings,
        ILogger<BybitSpotExchangeAdapter> logger,
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
            ? BybitEnvironment.Testnet
            : BybitEnvironment.Live;

        _restClient = new BybitRestClient(options =>
        {
            options.Environment = environment;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BybitCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        _socketClient = new BybitSocketClient(options =>
        {
            options.Environment = environment;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BybitCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        var symbolInfo = await _restClient.V5Api.ExchangeData.GetSpotSymbolsAsync(_symbol, cancellationToken);
        if (!symbolInfo.Success || symbolInfo.Data is null)
        {
            throw new InvalidOperationException($"Unable to load Bybit symbol metadata for {_symbol}: {symbolInfo.Error}");
        }

        var symbol = symbolInfo.Data.List.Single();
        Rules = MapRules(symbol);

        _orderBook = new BybitSymbolOrderBook(
            _symbol,
            Category.Spot,
            options =>
            {
                options.Limit = ResolveSubscriptionDepth(_depth);
                options.InitialDataTimeout = TimeSpan.FromSeconds(30);
            },
            _loggerFactory,
            _socketClient);

        _logger.LogInformation("Bybit adapter initialized for {Symbol}", _symbol);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_orderBook);
        var result = await _orderBook.StartAsync(cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to start Bybit order book for {_symbol}: {result.Error}");
        }

        _logger.LogInformation("Bybit order book started for {Symbol}", _symbol);
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

        return new ExchangeMarketSnapshot(
            ExchangeId.Bybit,
            Rules,
            new OrderBookSnapshot(
                _symbol,
                MapStatus(_orderBook.Status),
                DateTimeOffset.UtcNow,
                _orderBook.UpdateTime,
                _orderBook.UpdateServerTime,
                _orderBook.DataAge ?? TimeSpan.MaxValue,
                _orderBook.Bids.Take(_depth).Select(x => new OrderBookLevel(x.Price, x.Quantity)).ToArray(),
                _orderBook.Asks.Take(_depth).Select(x => new OrderBookLevel(x.Price, x.Quantity)).ToArray()));
    }

    public void Dispose()
    {
        _orderBook?.Dispose();
        _socketClient?.Dispose();
        _restClient?.Dispose();
    }

    private static ExchangeSymbolRules MapRules(BybitSpotSymbol symbol) =>
        new(
            ExchangeId.Bybit,
            symbol.Name,
            symbol.BaseAsset,
            symbol.QuoteAsset,
            symbol.LotSizeFilter?.BasePrecision ?? 0m,
            symbol.LotSizeFilter?.MinOrderQuantity ?? 0m,
            symbol.LotSizeFilter?.MaxOrderQuantity ?? decimal.MaxValue,
            symbol.PriceFilter?.TickSize ?? 0m,
            symbol.LotSizeFilter?.MinOrderValue ?? 0m,
            symbol.LotSizeFilter?.BasePrecision,
            symbol.LotSizeFilter?.QuotePrecision);

    private static int ResolveSubscriptionDepth(int configuredDepth) =>
        configuredDepth switch
        {
            <= 1 => 1,
            <= 50 => 50,
            <= 200 => 200,
            _ => 1000
        };

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
}
