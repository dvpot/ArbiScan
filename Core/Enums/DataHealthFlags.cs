namespace ArbiScan.Core.Enums;

[Flags]
public enum DataHealthFlags
{
    None = 0,
    BinanceMissing = 1 << 0,
    BybitMissing = 1 << 1,
    BinanceStale = 1 << 2,
    BybitStale = 1 << 3,
    BinanceUnhealthy = 1 << 4,
    BybitUnhealthy = 1 << 5
}
