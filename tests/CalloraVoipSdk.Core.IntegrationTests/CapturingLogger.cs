using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Minimal thread-safe <see cref="ILogger"/> that records emitted entries so tests can assert
/// on log level and message. Logging happens on background tasks, hence the lock.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly object _sync = new();
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_sync)
                return _entries.ToArray();
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_sync)
            _entries.Add((logLevel, message));
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
