namespace ArbiScan.Core.Enums;

[Flags]
public enum DataHealthFlags
{
    None = 0,
    BinanceOutOfSync = 1 << 0,
    BybitOutOfSync = 1 << 1,
    BinanceStale = 1 << 2,
    BybitStale = 1 << 3,
    MissingSymbolRules = 1 << 4,
    MissingOrderBook = 1 << 5,
    InsufficientDepth = 1 << 6,
    Degraded = 1 << 7,
    BybitQuietMarket = 1 << 8
}
