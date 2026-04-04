using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed record OpportunityWindowEvent(
    string WindowId,
    string Symbol,
    ArbitrageDirection Direction,
    decimal TestNotionalUsd,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset ClosedAtUtc,
    long DurationMs,
    decimal MaxGrossSpreadUsd,
    decimal MaxNetEdgeUsd,
    decimal AverageNetEdgeUsd,
    int ObservationCount,
    SignalClass FinalWindowClass);
