using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Models;

public sealed class ActiveOpportunityWindow
{
    public required string WindowId { get; init; }
    public required string Symbol { get; init; }
    public required ArbitrageDirection Direction { get; init; }
    public required decimal TestNotionalUsd { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required decimal MaxGrossSpreadUsd { get; set; }
    public required decimal MaxNetEdgeUsd { get; set; }
    public required decimal NetEdgeSumUsd { get; set; }
    public required int ObservationCount { get; set; }
    public required SignalClass StrongestSignalClass { get; set; }

    public long LifetimeMs(DateTimeOffset currentTimestampUtc) =>
        (long)(currentTimestampUtc - StartedAtUtc).TotalMilliseconds;
}
