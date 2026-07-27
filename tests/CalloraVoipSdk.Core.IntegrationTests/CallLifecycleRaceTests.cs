using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Lifecycle/event correctness for the domain <see cref="Call"/> aggregate (Issue #17):
/// the best-effort BYE on <see cref="Call.Dispose"/> must reach the wire before the channel is torn
/// down (no BYE-vs-dispose race), and a remote hold indication must only raise
/// <see cref="Call.HoldStateChanged"/> when the hold state actually changes.
/// </summary>
public sealed class CallLifecycleRaceTests
{
    private static Call CallInState(ICallChannel channel, params CallState[] transitions)
    {
        var call = new Call(
            CallId.New(), CallDirection.Outbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        foreach (var state in transitions)
            call.TransitionTo(state);
        return call;
    }

    [Fact]
    public async Task Dispose_of_an_active_call_completes_the_BYE_before_disposing_the_channel()
    {
        var channel = new RecordingChannel { HangupDelay = TimeSpan.FromMilliseconds(60) };
        var call = CallInState(channel, CallState.Dialing, CallState.Ringing, CallState.Connected);

        call.Dispose();

        // Dispose sequences the channel teardown after the hangup on a detached task; wait for it.
        await channel.DisposeCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(channel.HangupCalled); // a best-effort BYE was attempted
        // The fix: the channel is disposed only AFTER the BYE has completed. Before it, Dispose tore the
        // channel down synchronously while the fire-and-forget hangup was still in flight (the BYE raced
        // the transport teardown and the dialog was left dangling on the peer).
        Assert.True(channel.HangupHadCompletedAtDispose);
    }

    [Fact]
    public void Remote_hold_only_raises_HoldStateChanged_on_a_real_state_change()
    {
        var channel = new RecordingChannel();
        var call = CallInState(channel, CallState.Dialing, CallState.Ringing); // Ringing, not yet Connected
        var holdEvents = new List<bool>();
        call.HoldStateChanged += (_, e) => holdEvents.Add(e.IsOnHold);
        var remoteHold = channel.Callbacks!.OnRemoteHold!;

        // Remote hold before Connected → no CallState transition → must not raise the event.
        remoteHold(true);
        Assert.Empty(holdEvents);
        Assert.Equal(CallState.Ringing, call.State);

        // A real remote hold once Connected → OnHold + exactly one event.
        call.TransitionTo(CallState.Connected);
        remoteHold(true);
        Assert.Equal(new[] { true }, holdEvents);
        Assert.Equal(CallState.OnHold, call.State);

        // Duplicate hold while already OnHold → no change → still one event.
        remoteHold(true);
        Assert.Equal(new[] { true }, holdEvents);

        // A real remote unhold → Connected + a second event.
        remoteHold(false);
        Assert.Equal(new[] { true, false }, holdEvents);
        Assert.Equal(CallState.Connected, call.State);

        // Duplicate unhold → no change → still two events.
        remoteHold(false);
        Assert.Equal(new[] { true, false }, holdEvents);
    }

    // Records the hangup/dispose ordering and captures the bound callbacks; everything else is inert.
    private sealed class RecordingChannel : ICallChannel
    {
        private volatile bool _hangupCompleted;
        private int _disposeCount;

        public CallChannelCallbacks? Callbacks { get; private set; }
        public bool HangupCalled { get; private set; }
        public bool HangupHadCompletedAtDispose { get; private set; }
        public TimeSpan HangupDelay { get; set; } = TimeSpan.FromMilliseconds(40);
        public TaskCompletionSource DisposeCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BindCallbacks(CallChannelCallbacks callbacks) => Callbacks = callbacks;

        public async Task HangupAsync()
        {
            HangupCalled = true;
            await Task.Delay(HangupDelay).ConfigureAwait(false);
            _hangupCompleted = true;
        }

        public void Dispose()
        {
            if (_disposeCount++ == 0)
            {
                HangupHadCompletedAtDispose = _hangupCompleted;
                DisposeCalled.TrySetResult();
            }
        }

        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
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

#pragma warning disable CS0067 // no media negotiation exercised in these tests
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }
}
