using ArbiScan.Core.Configuration;
using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;

namespace ArbiScan.Exchanges.Binance;

public sealed class BinanceSpotExchangeAdapter : IExchangeAdapter, IDisposable
{
    private readonly string _symbol;
    private readonly ExchangeConnectionSettings _connectionSettings;
    private readonly ILogger<BinanceSpotExchangeAdapter> _logger;

    private BinanceRestClient? _restClient;
    private BinanceSocketClient? _socketClient;
    private UpdateSubscription? _subscription;
    private decimal? _bestBidPrice;
    private decimal? _bestBidQuantity;
    private decimal? _bestAskPrice;
    private decimal? _bestAskQuantity;
    private DateTimeOffset? _lastUpdateUtc;
    private bool _isConnected;
    private int _errorCount;

    public BinanceSpotExchangeAdapter(
        string symbol,
        ExchangeConnectionSettings connectionSettings,
        ILogger<BinanceSpotExchangeAdapter> logger)
    {
        _symbol = symbol;
        _connectionSettings = connectionSettings;
        _logger = logger;
    }

    public ExchangeId Exchange => ExchangeId.Binance;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _restClient = new BinanceRestClient(options =>
        {
            options.Environment = BinanceEnvironment.Live;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BinanceCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        _socketClient = new BinanceSocketClient(options =>
        {
            options.Environment = BinanceEnvironment.Live;
            if (!string.IsNullOrWhiteSpace(_connectionSettings.ApiKey) && !string.IsNullOrWhiteSpace(_connectionSettings.ApiSecret))
            {
                options.ApiCredentials = new BinanceCredentials(_connectionSettings.ApiKey, _connectionSettings.ApiSecret);
            }
        });

        var exchangeInfo = await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(_symbol, null, cancellationToken);
        if (!exchangeInfo.Success || exchangeInfo.Data is null || !exchangeInfo.Data.Symbols.Any())
        {
            throw new InvalidOperationException($"Unable to load Binance symbol metadata for {_symbol}: {exchangeInfo.Error}");
        }

        _logger.LogInformation("Binance best-bid-ask adapter initialized for {Symbol}", _symbol);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_socketClient);

        var result = await _socketClient.SpotApi.ExchangeData.SubscribeToBookTickerUpdatesAsync(
            _symbol,
            HandleUpdate,
            cancellationToken);

        if (!result.Success || result.Data is null)
        {
            throw new InvalidOperationException($"Failed to start Binance best-bid-ask stream for {_symbol}: {result.Error}");
        }

        _subscription = result.Data;
        _isConnected = true;
        _logger.LogInformation("Binance best-bid-ask stream started for {Symbol}", _symbol);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.CloseAsync();
            _subscription = null;
        }

        _isConnected = false;
    }

    public ExchangeMarketSnapshot? GetSnapshot()
    {
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
            var result = await _restClient.SpotApi.ExchangeData.GetTickerAsync(_symbol, cancellationToken);
            if (!result.Success || result.Data is null)
            {
                return (false, $"Binance REST probe failed: {result.Error}");
            }

            if (result.Data.BestBidPrice <= 0m || result.Data.BestAskPrice <= 0m)
            {
                return (false, "Binance REST probe returned empty top-of-book");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binance REST health probe failed for {Symbol}", _symbol);
            return (false, $"Binance REST probe exception: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _socketClient?.Dispose();
        _restClient?.Dispose();
    }

    private void HandleUpdate(DataEvent<BinanceStreamBookPrice> update)
    {
        try
        {
            _bestBidPrice = update.Data.BestBidPrice;
            _bestBidQuantity = update.Data.BestBidQuantity;
            _bestAskPrice = update.Data.BestAskPrice;
            _bestAskQuantity = update.Data.BestAskQuantity;
            _lastUpdateUtc = DateTimeOffset.UtcNow;
            _isConnected = true;
        }
        catch (Exception ex)
        {
            _errorCount++;
            _logger.LogError(ex, "Binance quote update processing failed for {Symbol}", _symbol);
        }
    }
}
