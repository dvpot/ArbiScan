using ArbiScan.Core.Enums;
using ArbiScan.Core.Interfaces;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Services;

public sealed class FillableSizeCalculator : IFillableSizeCalculator
{
    public SweepResult SweepQuote(OrderBookSnapshot book, decimal quoteAmount, TradeSide side)
    {
        if (quoteAmount <= 0m)
        {
            return new SweepResult(side, quoteAmount, 0m, 0m, 0m, null, false, 0);
        }

        var levels = GetLevels(book, side);
        var remainingQuote = quoteAmount;
        var executedBase = 0m;
        var executedQuote = 0m;
        var consumedLevels = 0;
        decimal? topPrice = null;

        foreach (var level in levels)
        {
            if (level.Quantity <= 0m || level.Price <= 0m)
            {
                continue;
            }

            topPrice ??= level.Price;
            var maxQuoteAtLevel = level.Price * level.Quantity;
            var quoteTaken = Math.Min(remainingQuote, maxQuoteAtLevel);
            if (quoteTaken <= 0m)
            {
                continue;
            }

            executedBase += quoteTaken / level.Price;
            executedQuote += quoteTaken;
            remainingQuote -= quoteTaken;
            consumedLevels++;

            if (remainingQuote <= 0m)
            {
                break;
            }
        }

        var averagePrice = executedBase > 0m ? executedQuote / executedBase : 0m;
        return new SweepResult(side, quoteAmount, executedBase, executedQuote, averagePrice, topPrice, remainingQuote <= 0m, consumedLevels);
    }

    public SweepResult SweepBase(OrderBookSnapshot book, decimal baseAmount, TradeSide side)
    {
        if (baseAmount <= 0m)
        {
            return new SweepResult(side, baseAmount, 0m, 0m, 0m, null, false, 0);
        }

        var levels = GetLevels(book, side);
        var remainingBase = baseAmount;
        var executedBase = 0m;
        var executedQuote = 0m;
        var consumedLevels = 0;
        decimal? topPrice = null;

        foreach (var level in levels)
        {
            if (level.Quantity <= 0m || level.Price <= 0m)
            {
                continue;
            }

            topPrice ??= level.Price;
            var baseTaken = Math.Min(remainingBase, level.Quantity);
            if (baseTaken <= 0m)
            {
                continue;
            }

            executedBase += baseTaken;
            executedQuote += baseTaken * level.Price;
            remainingBase -= baseTaken;
            consumedLevels++;

            if (remainingBase <= 0m)
            {
                break;
            }
        }

        var averagePrice = executedBase > 0m ? executedQuote / executedBase : 0m;
        return new SweepResult(side, baseAmount, executedBase, executedQuote, averagePrice, topPrice, remainingBase <= 0m, consumedLevels);
    }

    public decimal GetAvailableBaseLiquidity(OrderBookSnapshot book, TradeSide side) =>
        GetLevels(book, side).Sum(level => level.Quantity);

    private static IReadOnlyList<OrderBookLevel> GetLevels(OrderBookSnapshot book, TradeSide side) =>
        side == TradeSide.Buy ? book.Asks : book.Bids;
}
