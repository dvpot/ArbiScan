using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record SweepResult(
    TradeSide Side,
    decimal RequestedAmount,
    decimal ExecutedBaseQuantity,
    decimal ExecutedQuoteQuantity,
    decimal AveragePrice,
    decimal? TopPrice,
    bool FullyFilled,
    int ConsumedLevels);
