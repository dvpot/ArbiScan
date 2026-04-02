using Microsoft.Extensions.Logging;

namespace ArbiScan.Infrastructure.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsPath;
    private readonly LogLevel _minimumLevel;
    private readonly object _sync = new();

    public RollingFileLoggerProvider(string logsPath, LogLevel minimumLevel)
    {
        _logsPath = logsPath;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_logsPath);
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(_logsPath, categoryName, _minimumLevel, _sync);

    public void Dispose()
    {
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly string _logsPath;
        private readonly string _categoryName;
        private readonly LogLevel _minimumLevel;
        private readonly object _sync;

        public RollingFileLogger(string logsPath, string categoryName, LogLevel minimumLevel, object sync)
        {
            _logsPath = logsPath;
            _categoryName = categoryName;
            _minimumLevel = minimumLevel;
            _sync = sync;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var filePath = Path.Combine(_logsPath, $"application-{timestamp:yyyyMMdd}.log");
            var message = formatter(state, exception);
            var line = $"{timestamp:O} [{logLevel}] {_categoryName}: {message}";

            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            lock (_sync)
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
