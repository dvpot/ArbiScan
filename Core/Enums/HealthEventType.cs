namespace ArbiScan.Core.Enums;

public enum HealthEventType
{
    ApplicationStarted = 0,
    ApplicationStopping = 1,
    ExchangeConnected = 2,
    ExchangeRecovered = 3,
    ExchangeError = 4,
    StaleQuotesDetected = 5,
    StaleQuotesRecovered = 6
}
