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
/// #17.2 regression: the line starts in <see cref="LineState.Unregistered"/> and only later transitions to
/// <see cref="LineState.Registering"/>. The convenience connect wait must NOT treat that initial Unregistered
/// as a failure — a line that registers successfully (Registering → Registered) yields Registered, and one that
/// genuinely fails (Registering → Failed) still yields Failed.
/// </summary>
public sealed class SdkConvenienceRegistrationInitialStateTests
{
    [Fact]
    public async Task RegisterAndWait_LineThatRegistersAfterInitialUnregistered_YieldsRegistered()
    {
        // The whole transition sequence runs on a background hop, so the orchestrator's synchronous first read
        // of line.State observes the INITIAL Unregistered — exactly the real-world window (StartRegistration
        // schedules Registering asynchronously). Before the fix, that initial Unregistered short-circuited the
        // wait to Failed even though the line goes on to register successfully.
        var channel = new ScriptedLineChannel(async onState =>
        {
            await Task.Yield();
            await Task.Delay(30).ConfigureAwait(false);
            onState(LineState.Registering);
            await Task.Delay(30).ConfigureAwait(false);
            onState(LineState.Registered);
        });

        using var orchestrator = Orchestrator(channel);

        var outcome = await orchestrator.RegisterAndWaitAsync(
            new SipAccount { Username = "u", SipServer = "s" },
            TimeSpan.FromSeconds(5), failFastOnRegistrationFailed: false, CancellationToken.None);

        Assert.Equal(LineConnectStatus.Registered, outcome.Status);
    }

    [Fact]
    public async Task RegisterAndWait_LineThatFailsAfterRegistering_YieldsFailed()
    {
        var channel = new ScriptedLineChannel(async onState =>
        {
            await Task.Yield();
            await Task.Delay(30).ConfigureAwait(false);
            onState(LineState.Registering);
            await Task.Delay(30).ConfigureAwait(false);
            onState(LineState.Failed);
        });

        using var orchestrator = Orchestrator(channel);

        var outcome = await orchestrator.RegisterAndWaitAsync(
            new SipAccount { Username = "u", SipServer = "s" },
            TimeSpan.FromSeconds(5), failFastOnRegistrationFailed: false, CancellationToken.None);

        Assert.Equal(LineConnectStatus.Failed, outcome.Status);
    }

    private static SdkConvenienceOrchestrator Orchestrator(ScriptedLineChannel channel) =>
        new(new PhoneLineManager(account => new PhoneLine(
                account, channel, new NoopCallRegistry(), maxCalls: 0, NullLoggerFactory.Instance)),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);

    // Drives a caller-supplied state-transition script through the PhoneLine's registration callback.
    private sealed class ScriptedLineChannel : ILineChannel
    {
        private readonly Func<Action<LineState>, Task> _script;

        public ScriptedLineChannel(Func<Action<LineState>, Task> script) => _script = script;

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
            => _ = _script(onStateChange);

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
