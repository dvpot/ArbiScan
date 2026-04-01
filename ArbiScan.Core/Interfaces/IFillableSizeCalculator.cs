using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Core.Interfaces;

public interface IFillableSizeCalculator
{
    SweepResult SweepQuote(OrderBookSnapshot book, decimal quoteAmount, TradeSide side);
    SweepResult SweepBase(OrderBookSnapshot book, decimal baseAmount, TradeSide side);
    decimal GetAvailableBaseLiquidity(OrderBookSnapshot book, TradeSide side);
}
