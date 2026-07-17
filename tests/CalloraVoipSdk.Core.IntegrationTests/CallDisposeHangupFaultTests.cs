using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Disposing an active call issues a best-effort BYE. Dispose is synchronous so the hangup task
/// cannot be awaited — but a faulted hangup must not vanish as an unobserved task exception; the
/// fault is observed and logged at warning level.
/// </summary>
public sealed class CallDisposeHangupFaultTests
{
    [Fact]
    public async Task Dispose_logs_a_warning_when_the_best_effort_hangup_faults()
    {
        var logger = new CapturingCallLogger();
        // A channel with no attached session: HangupAsync throws (EnsureSession) → the best-effort
        // hangup on dispose faults, which must be logged rather than swallowed.
        using var channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        var call = new Call(
            CallId.New(),
            CallDirection.Outbound,
            "sip:remote@test.invalid",
            channel,
            new FakePhoneLine(),
            logger);
        call.TransitionTo(CallState.Ringing);
        call.TransitionTo(CallState.Connected); // not Terminated → dispose hangs up

        call.Dispose();

        // The fault continuation runs asynchronously on the thread pool; wait for the warning.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Warnings.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Contains(logger.Warnings, w => w.Contains("hangup", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingCallLogger : ILogger<Call>
    {
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (_warnings) _warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
