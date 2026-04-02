using ArbiScan.Core.Enums;

namespace ArbiScan.Core.Services;

public sealed class QuoteStalenessTracker
{
    private readonly Dictionary<ExchangeId, DateTimeOffset> _thresholdExceededSinceUtc = new();

    public bool IsStale(
        ExchangeId exchange,
        DateTimeOffset capturedAtUtc,
        TimeSpan dataAge,
        TimeSpan staleThreshold,
        TimeSpan confirmationWindow)
    {
        if (dataAge <= staleThreshold)
        {
            _thresholdExceededSinceUtc.Remove(exchange);
            return false;
        }

        var thresholdExceededSinceUtc = capturedAtUtc - (dataAge - staleThreshold);
        if (_thresholdExceededSinceUtc.TryGetValue(exchange, out var existingExceededSinceUtc) &&
            existingExceededSinceUtc < thresholdExceededSinceUtc)
        {
            thresholdExceededSinceUtc = existingExceededSinceUtc;
        }

        _thresholdExceededSinceUtc[exchange] = thresholdExceededSinceUtc;
        return capturedAtUtc - thresholdExceededSinceUtc >= confirmationWindow;
    }

    public void Reset(ExchangeId exchange) => _thresholdExceededSinceUtc.Remove(exchange);
}
