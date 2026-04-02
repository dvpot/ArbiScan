using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record TradeLegEstimate(
    ExchangeId Exchange,
    TradeSide Side,
    decimal Quantity,
    decimal AveragePrice,
    decimal GrossQuoteAmountUsd,
    decimal FeeUsd,
    decimal NetQuoteAmountUsd);
