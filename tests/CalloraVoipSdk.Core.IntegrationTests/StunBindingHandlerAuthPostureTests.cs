using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Authentication-posture gate for <see cref="StunBindingRequestHandler"/> (HARD-A9). Constructing
/// the handler without any credential mechanism answers every Binding Request unauthenticated —
/// legitimate for a public STUN endpoint, a spoofing risk otherwise — so the choice must be surfaced
/// with a warning; a credentialed handler must stay silent.
/// </summary>
public sealed class StunBindingHandlerAuthPostureTests
{
    [Fact]
    public void No_auth_constructor_warns_that_binding_is_unauthenticated()
    {
        var capturing = new CapturingLogger();

        _ = new StunBindingRequestHandler(
            new StunMessageCodec(),
            new TypedLogger<StunBindingRequestHandler>(capturing));

        Assert.Contains(
            capturing.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("without authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Provider_constructor_with_null_provider_warns()
    {
        var capturing = new CapturingLogger();

        _ = new StunBindingRequestHandler(
            new StunMessageCodec(),
            credentialProvider: null,
            defaultRealm: null,
            nonceManager: null,
            thirdPartyAuthorization: null,
            new TypedLogger<StunBindingRequestHandler>(capturing));

        Assert.Contains(
            capturing.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("without authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credentialed_constructor_does_not_warn()
    {
        var capturing = new CapturingLogger();
        var credentials = new StunCredentials { Username = "alice", Password = "s3cret" }; // short-term

        _ = new StunBindingRequestHandler(
            new StunMessageCodec(),
            credentials,
            new TypedLogger<StunBindingRequestHandler>(capturing));

        Assert.DoesNotContain(
            capturing.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase));
    }

    // Adapts the non-generic CapturingLogger to the ILogger<T> the handler requires.
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
