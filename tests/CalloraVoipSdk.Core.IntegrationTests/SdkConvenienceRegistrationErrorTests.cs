using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F005b regression: a fast permanent registration failure whose <c>LineReconnectFailed</c> event fires
/// during <c>Register</c> — before the convenience connect flow subscribes — must still surface the auth
/// cause as the outcome error. The orchestrator reads it from the line's recorded state
/// (<see cref="IPhoneLine.LastReconnectFailure"/>), not the missed event, so it is race-free.
/// </summary>
public sealed class SdkConvenienceRegistrationErrorTests
{
    [Fact]
    public async Task RegisterAndWait_FastFailureBeforeSubscribe_SurfacesAuthErrorFromLineState()
    {
        // The line fails (auth rejected) synchronously inside PhoneLineManager.Register — the
        // LineReconnectFailed event fires before the orchestrator can subscribe. Before the fix the
        // orchestrator relied on catching that event and returned Error == null; now it falls back to
        // line.LastReconnectFailure (recorded as state before the Failed transition).
        using var orchestrator = new SdkConvenienceOrchestrator(
            new PhoneLineManager(account => new PhoneLine(
                account, new FastFailLineChannel(), new NoopCallRegistry(), maxCalls: 0, NullLoggerFactory.Instance)),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);

        var outcome = await orchestrator.RegisterAndWaitAsync(
            new SipAccount { Username = "u", SipServer = "s" },
            TimeSpan.FromSeconds(5), failFastOnRegistrationFailed: false, CancellationToken.None);

        Assert.Equal(LineConnectStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
    }

    // Simulates a fast permanent auth rejection: records the failure reason and transitions to Failed
    // synchronously during Register — before any convenience consumer subscribes (missed event).
    private sealed class FastFailLineChannel : ILineChannel
    {
        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        {
            onReconnectFailed?.Invoke(ReregisterFailReason.AuthenticationFailed, 3);
            onStateChange(LineState.Failed);
        }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => throw new NotSupportedException();
        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    private sealed class NoopAudioDevice : IAudioDevice
    {
        public string Name => "noop";
        public void Connect(IMediaReceiver receiver, IMediaSender sender, AudioConnectionParameters parameters) { }
        public void Disconnect() { }
    }
}
