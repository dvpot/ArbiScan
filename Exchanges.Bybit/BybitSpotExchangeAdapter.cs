using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.SymbolOrderBooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace ArbiScan.Exchanges.Bybit;

public sealed class BybitSpotExchangeAdapter : IExchangeAdapter, IDisposable
{
    private readonly string _symbol;
    private readonly ExchangeConnectionSettings _connectionSettings;
    private readonly ILogger<BybitSpotExchangeAdapter> _logger;

    private BybitRestClient? _restClient;
    private BybitSocketClient? _socketClient;
    private BybitSymbolOrderBook? _orderBook;
    private decimal? _bestBidPrice;
    private decimal? _bestBidQuantity;
    private decimal? _bestAskPrice;
    private decimal? _bestAskQuantity;
    private DateTimeOffset? _lastUpdateUtc;
    private bool _isConnected;
    private int _errorCount;

    public BybitSpotExchangeAdapter(
        string symbol,
        ExchangeConnectionSettings connectionSettings,
        ILogger<BybitSpotExchangeAdapter> logger)
    {
        _symbol = symbol;
        _connectionSettings = connectionSettings;
        _logger = logger;
    }

    public ExchangeId Exchange => ExchangeId.Bybit;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _restClient = new BybitRestClient(options =>
        {
            options.Environment = BybitEnvironment.Live;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BybitCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        _socketClient = new BybitSocketClient(options =>
        {
            options.Environment = BybitEnvironment.Live;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BybitCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        var symbolInfo = await _restClient.V5Api.ExchangeData.GetSpotSymbolsAsync(_symbol, cancellationToken);
        if (!symbolInfo.Success || symbolInfo.Data is null || !symbolInfo.Data.List.Any())
        {
            throw new InvalidOperationException($"Unable to load Bybit symbol metadata for {_symbol}: {symbolInfo.Error}");
        }

        _logger.LogInformation("Bybit best-bid-ask adapter initialized for {Symbol}", _symbol);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_socketClient);

        _orderBook ??= new BybitSymbolOrderBook(
            _symbol,
            Category.Spot,
            options =>
            {
                options.Limit = 1;
            },
            NullLoggerFactory.Instance,
            _socketClient);

        var result = await _orderBook.StartAsync(cancellationToken);
        if (!result.Success || !result.Data)
        {
            throw new InvalidOperationException($"Failed to start Bybit best-bid-ask stream for {_symbol}: {result.Error}");
        }

        CaptureTopOfBook();
        _isConnected = true;
        _logger.LogInformation("Bybit best-bid-ask stream started for {Symbol}", _symbol);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_orderBook is not null)
        {
            await _orderBook.StopAsync();
        }

        _isConnected = false;
    }

    public ExchangeMarketSnapshot? GetSnapshot()
    {
        CaptureTopOfBook();

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var dataAge = _lastUpdateUtc.HasValue
            ? capturedAtUtc - _lastUpdateUtc.Value
            : TimeSpan.MaxValue;

        return new ExchangeMarketSnapshot(
            Exchange,
            _symbol,
            capturedAtUtc,
            _bestBidPrice,
            _bestBidQuantity,
            _bestAskPrice,
            _bestAskQuantity,
            _lastUpdateUtc,
            dataAge < TimeSpan.Zero ? TimeSpan.Zero : dataAge,
            _isConnected,
            _errorCount);
    }

    public async Task<(bool Success, string? FailureReason)> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(_restClient);
            var result = await _restClient.V5Api.ExchangeData.GetSpotTickersAsync(_symbol, cancellationToken);
            if (!result.Success || result.Data is null || !result.Data.List.Any())
            {
                return (false, $"Bybit REST probe failed: {result.Error}");
            }

            var ticker = result.Data.List[0];
            if (ticker.BestBidPrice <= 0m || ticker.BestAskPrice <= 0m)
            {
                return (false, "Bybit REST probe returned empty top-of-book");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bybit REST health probe failed for {Symbol}", _symbol);
            return (false, $"Bybit REST probe exception: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _orderBook?.Dispose();
        _socketClient?.Dispose();
        _restClient?.Dispose();
    }

    private void CaptureTopOfBook()
    {
        try
        {
            if (_orderBook?.BestBid is not null)
            {
                _bestBidPrice = _orderBook.BestBid.Price;
                _bestBidQuantity = _orderBook.BestBid.Quantity;
            }

            if (_orderBook?.BestAsk is not null)
            {
                _bestAskPrice = _orderBook.BestAsk.Price;
                _bestAskQuantity = _orderBook.BestAsk.Quantity;
            }

            if (_orderBook?.UpdateLocalTime is not null)
            {
                _lastUpdateUtc = new DateTimeOffset(DateTime.SpecifyKind(_orderBook.UpdateLocalTime.Value, DateTimeKind.Utc));
            }

            _isConnected = (_orderBook?.BestBid is not null || _orderBook?.BestAsk is not null);
        }
        catch (Exception ex)
        {
            _errorCount++;
            _logger.LogError(ex, "Bybit quote update processing failed for {Symbol}", _symbol);
        }
    }
}
