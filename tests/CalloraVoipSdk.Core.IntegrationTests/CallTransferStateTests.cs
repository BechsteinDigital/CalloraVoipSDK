using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #9 — transfer state-machine safety on the <see cref="Call"/> aggregate:
/// <list type="bullet">
/// <item><description>a blind/attended transfer whose channel signaling throws (timeout/cancel/transport) must
/// NOT leave the call wedged in <c>Transferring</c> — it reverts to <c>Connected</c> and rethrows;</description></item>
/// <item><description>attended transfer, like blind, requires the <c>Connected</c> precondition;</description></item>
/// <item><description>the state rules match the guards — a transfer starts only from <c>Connected</c>.</description></item>
/// </list>
/// </summary>
public sealed class CallTransferStateTests
{
    private static Call ConnectedCall(FakeTransferChannel channel)
    {
        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        call.TransitionTo(CallState.Ringing);
        call.TransitionTo(CallState.Connected);
        return call;
    }

    [Fact]
    public async Task A_blind_transfer_that_throws_reverts_to_connected_and_rethrows()
    {
        var channel = new FakeTransferChannel { OnTransfer = () => throw new TimeoutException("transfer timed out") };
        var call = ConnectedCall(channel);

        await Assert.ThrowsAsync<TimeoutException>(() => call.BlindTransferAsync("sip:target@test.invalid"));

        Assert.Equal(CallState.Connected, call.State); // reverted — NOT wedged in Transferring
    }

    [Fact]
    public async Task An_attended_transfer_that_throws_reverts_to_connected_and_rethrows()
    {
        var channel = new FakeTransferChannel { OnTransfer = () => throw new TimeoutException("transfer timed out") };
        var call = ConnectedCall(channel);
        var consultation = ConnectedCall(new FakeTransferChannel());

        await Assert.ThrowsAsync<TimeoutException>(() => call.AttendedTransferAsync(consultation));

        Assert.Equal(CallState.Connected, call.State);
    }

    [Fact]
    public async Task An_attended_transfer_from_a_non_connected_state_is_rejected_without_running_the_channel()
    {
        var channel = new FakeTransferChannel();
        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        call.TransitionTo(CallState.Ringing); // not Connected
        var consultation = ConnectedCall(new FakeTransferChannel());

        await Assert.ThrowsAsync<InvalidOperationException>(() => call.AttendedTransferAsync(consultation));

        Assert.Equal(CallState.Ringing, call.State); // unchanged
        Assert.False(channel.TransferInvoked);       // guard fired before the channel transfer ran
    }

    [Fact]
    public void The_state_rules_allow_a_transfer_only_from_connected()
    {
        Assert.True(CallStateRules.CanTransition(CallState.Connected, CallState.Transferring));
        Assert.False(CallStateRules.CanTransition(CallState.OnHold, CallState.Transferring)); // #9: reconciled to the guard
        Assert.True(CallStateRules.CanTransition(CallState.Transferring, CallState.Connected)); // the revert path
    }

    // Minimal channel: the transfer methods are configurable (throw or return a value); everything else is inert.
    private sealed class FakeTransferChannel : ICallChannel
    {
        public Func<bool>? OnTransfer { get; init; }
        public bool TransferInvoked { get; private set; }

        private Task<bool> RunTransfer()
        {
            TransferInvoked = true;
            if (OnTransfer is null)
                return Task.FromResult(true);
            try { return Task.FromResult(OnTransfer()); }
            catch (Exception ex) { return Task.FromException<bool>(ex); }
        }

        public Task<bool> BlindTransferAsync(string targetUri, TimeSpan timeout, CancellationToken ct) => RunTransfer();
        public Task<bool> AttendedTransferAsync(ICallChannel target, TimeSpan timeout, CancellationToken ct) => RunTransfer();

        public void BindCallbacks(CallChannelCallbacks callbacks) { }
        public void Dispose() { }

        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync() => Task.CompletedTask;
        public Task HoldAsync() => Task.CompletedTask;
        public Task UnholdAsync() => Task.CompletedTask;
        public Task SendDtmfAsync(byte dtmfCode) => Task.CompletedTask;
        public Task RejectAsync(int statusCode, string? reasonPhrase, CancellationToken ct) => Task.CompletedTask;
        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode, CancellationToken ct) => Task.CompletedTask;
        public Task SendInfoAsync(string contentType, string body, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SendOptionsAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds, string? acceptHeader, string? body, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType, string? body, CancellationToken ct) => Task.FromResult(true);
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

#pragma warning disable CS0067 // no media negotiation in these transfer tests
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }
}
