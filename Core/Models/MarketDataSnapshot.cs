using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record MarketDataSnapshot(
    string Symbol,
    DateTimeOffset CapturedAtUtc,
    ExchangeMarketSnapshot? Binance,
    ExchangeMarketSnapshot? Bybit,
    DataHealthFlags HealthFlags)
{
    public ExchangeMarketSnapshot? GetExchange(ExchangeId exchange) =>
        exchange switch
        {
            ExchangeId.Binance => Binance,
            ExchangeId.Bybit => Bybit,
            _ => null
        };
}
