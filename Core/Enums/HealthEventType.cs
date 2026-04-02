namespace ArbiScan.Core.Enums;

public enum HealthEventType
{
    ApplicationStarted = 0,
    ApplicationStopping = 1,
    ExchangeStatusChanged = 2,
    ReconnectStarted = 3,
    ReconnectCompleted = 4,
    ResyncStarted = 5,
    ResyncCompleted = 6,
    StaleQuotesDetected = 7,
    StaleQuotesRecovered = 8,
    OverallHealthChanged = 9,
    PersistenceWarning = 10
}
