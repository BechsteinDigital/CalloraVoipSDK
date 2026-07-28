using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed class CalloraCapacityWarningLoggerFactory : ILoggerFactory
{
    private const string RtcpMonitorCategory = "CallRtcpQualityMonitor";
    private readonly ConcurrentQueue<string> _warnings;

    public CalloraCapacityWarningLoggerFactory(ConcurrentQueue<string> warnings) =>
        _warnings = warnings;

    public ILogger CreateLogger(string categoryName) =>
        categoryName.EndsWith(RtcpMonitorCategory, StringComparison.Ordinal)
            ? new WarningLogger(categoryName, _warnings)
            : DisabledLogger.Instance;

    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("The capacity logger factory has a fixed in-memory provider.");

    public void Dispose()
    {
    }

    private sealed class WarningLogger(
        string category,
        ConcurrentQueue<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

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

            var exceptionText = exception is null
                ? string.Empty
                : $" [{exception.GetType().Name}: {exception.Message}]";
            warnings.Enqueue($"{category}: {formatter(state, exception)}{exceptionText}");
        }
    }

    private sealed class DisabledLogger : ILogger
    {
        public static DisabledLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
