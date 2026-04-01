using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record HealthEvent(
    DateTimeOffset TimestampUtc,
    HealthEventType EventType,
    ExchangeId? Exchange,
    DataHealthFlags Flags,
    bool IsHealthyAfterEvent,
    string Message);
