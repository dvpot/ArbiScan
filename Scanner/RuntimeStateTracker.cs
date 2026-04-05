using ArbiScan.Core.Enums;
using ArbiScan.Core.Models;

namespace ArbiScan.Scanner;

public sealed class RuntimeStateTracker
{
    private readonly Lock _sync = new();
    private RuntimeStatusSnapshot _snapshot = RuntimeStatusSnapshot.CreateInitial();

    public void MarkStarted(string symbol)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Symbol = symbol,
                StartedAtUtc = DateTimeOffset.UtcNow,
                State = "running",
                StatusMessage = "Сканер запущен."
            };
        }
    }

    public void UpdateLoop(MarketDataSnapshot snapshot, OpportunityEvaluation? bestEvaluation)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Symbol = snapshot.Symbol,
                LastLoopAtUtc = snapshot.CapturedAtUtc,
                HealthFlags = snapshot.HealthFlags,
                Binance = snapshot.Binance,
                Bybit = snapshot.Bybit,
                BestEvaluation = bestEvaluation,
                StatusMessage = "Сканер работает."
            };
        }
    }

    public void MarkStopping(string reason)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                State = "stopping",
                StatusMessage = reason
            };
        }
    }

    public void MarkStopped(string reason)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                State = "stopped",
                StatusMessage = reason
            };
        }
    }

    public void MarkCriticalError(Exception ex)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                State = "error",
                StatusMessage = ex.Message,
                LastCriticalError = ex.Message
            };
        }
    }

    public RuntimeStatusSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }
}

public sealed record RuntimeStatusSnapshot(
    string State,
    string Symbol,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastLoopAtUtc,
    DataHealthFlags HealthFlags,
    ExchangeMarketSnapshot? Binance,
    ExchangeMarketSnapshot? Bybit,
    OpportunityEvaluation? BestEvaluation,
    string StatusMessage,
    string? LastCriticalError)
{
    public static RuntimeStatusSnapshot CreateInitial() =>
        new(
            "starting",
            "unknown",
            null,
            null,
            DataHealthFlags.None,
            null,
            null,
            null,
            "Сканер ещё не вышел в рабочий цикл.",
            null);
}
