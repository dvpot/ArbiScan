using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record ExchangeSymbolRules(
    ExchangeId Exchange,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal QuantityStep,
    decimal MinimumQuantity,
    decimal MaximumQuantity,
    decimal TickSize,
    decimal MinimumNotional,
    decimal? BasePrecision = null,
    decimal? QuotePrecision = null);
