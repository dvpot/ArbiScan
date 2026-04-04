using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record ExchangeMarketSnapshot(
    ExchangeId Exchange,
    string Symbol,
    DateTimeOffset CapturedAtUtc,
    decimal? BestBidPrice,
    decimal? BestBidQuantity,
    decimal? BestAskPrice,
    decimal? BestAskQuantity,
    DateTimeOffset? LastUpdateUtc,
    TimeSpan DataAge,
    bool IsConnected,
    int ErrorCount)
{
    public bool HasQuote =>
        BestBidPrice.HasValue &&
        BestAskPrice.HasValue &&
        BestBidPrice.Value > 0m &&
        BestAskPrice.Value > 0m;
}
