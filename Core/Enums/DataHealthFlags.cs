namespace ArbiScan.Core.Enums;

[Flags]
public enum DataHealthFlags
{
    None = 0,
    BinanceMissing = 1 << 0,
    BybitMissing = 1 << 1,
    BinanceUnhealthy = 1 << 2,
    BybitUnhealthy = 1 << 3
}
