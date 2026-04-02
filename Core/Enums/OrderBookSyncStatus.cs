namespace ArbiScan.Core.Enums;

public enum OrderBookSyncStatus
{
    Unknown = 0,
    Disconnected = 1,
    Connecting = 2,
    Reconnecting = 3,
    Syncing = 4,
    Synced = 5,
    Disposing = 6,
    Disposed = 7
}
