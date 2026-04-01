using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Services;

public static class ArbitrageDirectionExtensions
{
    public static ExchangeId BuyExchange(this ArbitrageDirection direction) =>
        direction switch
        {
            ArbitrageDirection.BuyBinanceSellBybit => ExchangeId.Binance,
            ArbitrageDirection.BuyBybitSellBinance => ExchangeId.Bybit,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
        };

    public static ExchangeId SellExchange(this ArbitrageDirection direction) =>
        direction switch
        {
            ArbitrageDirection.BuyBinanceSellBybit => ExchangeId.Bybit,
            ArbitrageDirection.BuyBybitSellBinance => ExchangeId.Binance,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
        };
}
