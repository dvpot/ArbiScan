using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface ISymbolRulesProvider
{
    ExchangeSymbolRules? Rules { get; }
}
