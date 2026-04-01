using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public static class SymbolRulesNormalizer
{
    public static decimal GetExecutableQuantity(
        decimal desiredQuantity,
        ExchangeSymbolRules buyRules,
        ExchangeSymbolRules sellRules)
    {
        var roundedBuy = RoundDownToStep(desiredQuantity, buyRules.QuantityStep);
        var roundedSell = RoundDownToStep(desiredQuantity, sellRules.QuantityStep);
        var executable = Math.Min(roundedBuy, roundedSell);

        executable = Math.Min(executable, buyRules.MaximumQuantity);
        executable = Math.Min(executable, sellRules.MaximumQuantity);

        if (executable < buyRules.MinimumQuantity || executable < sellRules.MinimumQuantity)
        {
            return 0m;
        }

        return executable;
    }

    public static bool MeetsMinimums(ExchangeSymbolRules rules, decimal quantity, decimal quoteNotional) =>
        quantity >= rules.MinimumQuantity &&
        quantity <= rules.MaximumQuantity &&
        quoteNotional >= rules.MinimumNotional;

    public static decimal RoundDownToStep(decimal value, decimal step)
    {
        if (step <= 0m)
        {
            return value;
        }

        return decimal.Floor(value / step) * step;
    }
}
