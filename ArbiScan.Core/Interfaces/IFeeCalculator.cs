using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Interfaces;

public interface IFeeCalculator
{
    decimal CalculateFeeUsd(ExchangeId exchange, decimal grossQuoteAmountUsd);
}
