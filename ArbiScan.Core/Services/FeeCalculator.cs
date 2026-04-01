using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;

namespace ArbiScan.Core.Services;

public sealed class FeeCalculator : IFeeCalculator
{
    private readonly decimal _binanceTakerFeeRate;
    private readonly decimal _bybitTakerFeeRate;

    public FeeCalculator(decimal binanceTakerFeeRate, decimal bybitTakerFeeRate)
    {
        _binanceTakerFeeRate = binanceTakerFeeRate;
        _bybitTakerFeeRate = bybitTakerFeeRate;
    }

    public decimal CalculateFeeUsd(ExchangeId exchange, decimal grossQuoteAmountUsd)
    {
        var rate = exchange switch
        {
            ExchangeId.Binance => _binanceTakerFeeRate,
            ExchangeId.Bybit => _bybitTakerFeeRate,
            _ => throw new ArgumentOutOfRangeException(nameof(exchange), exchange, null)
        };

        return grossQuoteAmountUsd * rate;
    }
}
