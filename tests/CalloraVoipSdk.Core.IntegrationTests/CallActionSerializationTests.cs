using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Every public call action is "check the state, await the signaling round-trip, commit" — and without
/// serialisation plus a conditional commit, two of them interleave inside that await (#165 P2-4). Both pass
/// their guard, both drive the channel, and the second commits onto a state its caller never saw. The probe
/// in the review: hold_race_final_state=Terminated hold_events_after_termination=1 — the caller was told the
/// hold succeeded and a hold event was raised on a call that had already terminated.
/// </summary>
public sealed class CallActionSerializationTests
{
    private static (ICall Call, GatedCallChannel Channel) InboundCall(bool gateAnswer = false)
    {
        var registry = new CallManager();
        var lineChannel = new InboundCapturingLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", SipServer = "s" },
            lineChannel, registry, maxCalls: 0, NullLoggerFactory.Instance);

        ICall? inbound = null;
        line.IncomingCall += (_, e) => inbound = e.Call;

        var channel = new GatedCallChannel { GateAnswer = gateAnswer };
        lineChannel.RaiseInbound(channel, "sip:caller@remote.invalid");
        return (inbound!, channel);
    }

    [Fact]
    public async Task A_hold_overtaken_by_termination_neither_reports_success_nor_raises_its_event()
    {
        var (call, channel) = InboundCall();
        await call.AcceptAsync();

        var holdEvents = 0;
        call.HoldStateChanged += (_, _) => Interlocked.Increment(ref holdEvents);

        var hold = call.HoldAsync();
        Assert.True(await channel.HoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // A remote BYE arrives while the re-INVITE is in flight: the signaling path terminates the call
        // without waiting for the action that is mid-flight.
        channel.RaiseStateChange(CallState.Terminated);
        channel.ReleaseHold();

        await Assert.ThrowsAsync<InvalidOperationException>(() => hold);
        Assert.Equal(CallState.Terminated, call.State);
        Assert.Equal(0, Volatile.Read(ref holdEvents));
    }

    [Fact]
    public async Task Two_concurrent_holds_reach_the_channel_once()
    {
        var (call, channel) = InboundCall();
        await call.AcceptAsync();

        var first = call.HoldAsync();
        Assert.True(await channel.HoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // The second caller arrives while the first is still talking to the peer. Unserialised, it passed the
        // same Connected guard and drove a second re-INVITE.
        var second = call.HoldAsync();
        channel.ReleaseHold();

        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second); // the call is on hold by then
        Assert.Equal(1, channel.HoldCalls);
        Assert.Equal(CallState.OnHold, call.State);
    }

    [Fact]
    public async Task A_hangup_racing_an_accept_leaves_the_call_terminated_exactly_once()
    {
        var (call, channel) = InboundCall(gateAnswer: true);

        var accept = call.AcceptAsync();
        Assert.True(await channel.AnswerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var terminations = 0;
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                Interlocked.Increment(ref terminations);
        };

        var hangup = call.HangupAsync(); // queues behind the accept instead of interleaving with it
        channel.ReleaseAnswer();

        await accept;
        await hangup;

        Assert.Equal(CallState.Terminated, call.State);
        Assert.Equal(1, Volatile.Read(ref terminations));
    }

    private sealed class GatedCallChannel : ICallChannel
    {
        private readonly TaskCompletionSource _holdGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CallChannelCallbacks? _callbacks;
        private int _holdCalls;

        /// <summary>Whether AnswerAsync blocks until <see cref="ReleaseAnswer"/>; only the accept race needs it.</summary>
        public bool GateAnswer { get; init; }

        public TaskCompletionSource<bool> HoldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AnswerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HoldCalls => Volatile.Read(ref _holdCalls);

        public void ReleaseHold() => _holdGate.TrySetResult();
        public void ReleaseAnswer() => _answerGate.TrySetResult();

        /// <summary>Drives the signaling-side state change the way an inbound BYE would.</summary>
        public void RaiseStateChange(CallState state) => _callbacks!.OnStateChange(state, null);

        public void BindCallbacks(CallChannelCallbacks callbacks) => _callbacks = callbacks;

        public async Task HoldAsync()
        {
            Interlocked.Increment(ref _holdCalls);
            HoldEntered.TrySetResult(true);
            await _holdGate.Task;
        }

        public async Task AnswerAsync(CancellationToken ct)
        {
            AnswerEntered.TrySetResult(true);
            if (GateAnswer)
                await _answerGate.Task;
        }

        public void Dispose() { }
        public Task HangupAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
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

#pragma warning disable CS0067 // no media negotiation exercised here
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }

    private sealed class InboundCapturingLineChannel : ILineChannel
    {
        private Action<ICallChannel, string>? _onInbound;

        public void RaiseInbound(ICallChannel channel, string remoteParty) => _onInbound!(channel, remoteParty);

        public void SetInboundHandler(Action<ICallChannel, string> onInbound) => _onInbound = onInbound;

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) => throw new NotSupportedException();
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
