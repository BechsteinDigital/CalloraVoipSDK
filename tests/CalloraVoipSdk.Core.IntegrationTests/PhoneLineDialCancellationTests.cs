using CalloraVoipSdk.Core.Application.Convenience;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Caller-cancellation while an outbound INVITE is still pending/ringing (RFC 3261 §9.1). When the
/// caller cancels the dial during ringing, <see cref="PhoneLine.DialAsync"/> must send a SIP CANCEL for
/// the in-flight INVITE (so the peer stops ringing rather than waiting for its own timeout) and must
/// KEEP the call reachable — it returns the now-terminated call instead of losing it to a rethrow — so
/// the convenience layer surfaces it as Canceled with a non-null call handle.
/// </summary>
public sealed class PhoneLineDialCancellationTests
{
    // Load-bearing: cancelling a ringing dial sends CANCEL (observed on the channel) and the returned
    // call is reachable in Terminated state — no exception escapes DialAsync.
    [Fact]
    public async Task DialAsync_CancelledWhileRinging_SendsCancel_AndReturnsTerminatedCall()
    {
        var callChannel = new HangupObservingCallChannel();
        var lineChannel = new BlockingRingingLineChannel(callChannel);
        var line = NewLine(lineChannel);

        using var cts = new CancellationTokenSource();
        callChannel.OnReachedRinging = cts.Cancel; // cancel exactly once the call is ringing

        var call = await line.DialAsync("sip:bob@example.com", ct: cts.Token);

        Assert.Equal(1, callChannel.HangupCount);      // CANCEL routed through the channel hangup
        Assert.Equal(CallState.Terminated, call.State); // call reachable and terminal
    }

    // End-to-end through the convenience orchestrator: a cancelled ringing dial maps to Canceled with a
    // non-null call handle (the pre-fix null-call regression is gone), and CANCEL was sent.
    [Fact]
    public async Task DialAndWait_CancelledWhileRinging_YieldsCanceled_WithReachableCall()
    {
        var callChannel = new HangupObservingCallChannel();
        var lineChannel = new BlockingRingingLineChannel(callChannel);
        var line = NewLine(lineChannel);

        using var orchestrator = new SdkConvenienceOrchestrator(
            new PhoneLineManager(_ => throw new NotSupportedException("no register here")),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);
        using var cts = new CancellationTokenSource();
        callChannel.OnReachedRinging = cts.Cancel;

        var outcome = await orchestrator.DialAndWaitUntilConnectedAsync(
            line, "sip:bob@example.com", dialOptions: null, TimeSpan.FromSeconds(30),
            hangupOnTimeout: false, hangupOnCancellation: false, cts.Token);

        Assert.Equal(CallConnectStatus.Canceled, outcome.Status);
        Assert.NotNull(outcome.Call);
        Assert.Equal(CallState.Terminated, outcome.Call!.State);
        Assert.True(callChannel.HangupCount >= 1); // CANCEL was sent for the pending INVITE
    }

    // F008 regression: the connect timeout (not a caller cancellation) fires while ringing. DialAsync
    // now CANCELs and returns the terminated call for the timeout's linked token exactly as for a caller
    // cancellation, so the convenience must distinguish the two and map the timeout to Timeout — not the
    // Failed the returned Terminated state would otherwise yield.
    [Fact]
    public async Task DialAndWait_ConnectTimeoutWhileRinging_YieldsTimeout_WithReachableCall()
    {
        var callChannel = new HangupObservingCallChannel();
        var lineChannel = new BlockingRingingLineChannel(callChannel);
        var line = NewLine(lineChannel);

        using var orchestrator = new SdkConvenienceOrchestrator(
            new PhoneLineManager(_ => throw new NotSupportedException("no register here")),
            new MediaManager(), new NoopAudioDevice(), NullLoggerFactory.Instance, videoDevice: null);

        // No caller cancellation — the short connect timeout elapses while the dial is still ringing.
        var outcome = await orchestrator.DialAndWaitUntilConnectedAsync(
            line, "sip:bob@example.com", dialOptions: null, TimeSpan.FromMilliseconds(200),
            hangupOnTimeout: false, hangupOnCancellation: false, CancellationToken.None);

        Assert.Equal(CallConnectStatus.Timeout, outcome.Status); // F008: Timeout, not Failed
        Assert.NotNull(outcome.Call);
        Assert.Equal(CallState.Terminated, outcome.Call!.State);
        Assert.True(callChannel.HangupCount >= 1);               // CANCEL was still sent for the pending INVITE
    }

    private static PhoneLine NewLine(ILineChannel channel)
    {
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new NoopCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);
        line.StartRegistration();
        return line;
    }

    // Reports Registered synchronously, drives the bound call to Ringing, then blocks on the dial token
    // — mimicking a 180-ringing INVITE that the caller cancels mid-flight. The cancelled token then
    // surfaces as an OperationCanceledException out of StartOutboundDialAsync, exactly as the real INVITE
    // transaction raises it.
    private sealed class BlockingRingingLineChannel(HangupObservingCallChannel callChannel) : ILineChannel
    {
        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
            => onStateChange(LineState.Registered);

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ICallChannel PrepareOutboundChannel(DialOptions options) => callChannel;

        public async Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct)
        {
            callChannel.Drive(CallState.Ringing);
            callChannel.OnReachedRinging?.Invoke();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<CalloraVoipSdk.Core.Domain.Messages.SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CalloraVoipSdk.Core.Domain.Publications.PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => Task.FromResult(new CalloraVoipSdk.Core.Domain.Publications.PublishResult(null, 0));
        public void Dispose() { }
    }

    // Captures the state-change callback, drives transitions, and counts HangupAsync calls — the
    // observable proxy for a SIP CANCEL on the pending outbound INVITE.
    private sealed class HangupObservingCallChannel : ICallChannel
    {
        private Action<CallState>? _onStateChange;

        public Action? OnReachedRinging { get; set; }
        public int HangupCount { get; private set; }

        public void Drive(CallState state) => _onStateChange?.Invoke(state);

        public void BindCallbacks(CallChannelCallbacks callbacks) => _onStateChange = callbacks.OnStateChange;

        public Task HangupAsync()
        {
            HangupCount++;
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds, string? acceptHeader, string? body, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType, string? body, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task SendAudioFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public void AddAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void RemoveAudioFrameListener(Action<CallAudioFrame> onFrame) { }
        public void AddVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame) { }
        public void DeliverInboundAudioFrame(CallAudioFrame frame) { }
        public void SetAudioSendDelegate(Func<CallAudioFrame, CancellationToken, Task>? sendDelegate) { }
        public void DeliverInboundVideoFrame(CallVideoFrame frame) { }
        public void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate) { }

#pragma warning disable CS0067 // no media negotiation exercised in these tests
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
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
