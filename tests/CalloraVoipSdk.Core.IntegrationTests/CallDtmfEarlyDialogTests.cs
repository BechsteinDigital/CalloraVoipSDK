using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F011 Slice 3d — DTMF in the early dialog. Since Slice 3b starts the media session already at
/// <see cref="CallState.Ringing"/> (before the 200 OK) and wires the DTMF send delegate there, the
/// domain-level state guard on <see cref="Call.SendDtmfAsync"/> must not reject DTMF in Ringing —
/// otherwise IVR navigation and AI-outbound bots cannot send tones while the peer is still alerting.
/// DTMF stays rejected in states that carry no (early or confirmed) media dialog.
/// </summary>
public sealed class CallDtmfEarlyDialogTests
{
    private static Call CallInState(FakeDtmfChannel channel, params CallState[] transitions)
    {
        var call = new Call(
            CallId.New(), CallDirection.Outbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        foreach (var state in transitions)
            call.TransitionTo(state);
        return call;
    }

    [Fact]
    public async Task SendDtmf_in_Ringing_early_dialog_is_allowed_and_reaches_the_channel()
    {
        var channel = new FakeDtmfChannel();
        var call = CallInState(channel, CallState.Dialing, CallState.Ringing);
        Assert.Equal(CallState.Ringing, call.State);

        await call.SendDtmfAsync(DtmfTone.FromCode(5));

        Assert.Equal(new byte[] { 5 }, channel.SentCodes);
    }

    [Fact]
    public async Task SendDtmf_in_Connected_is_allowed_and_reaches_the_channel()
    {
        var channel = new FakeDtmfChannel();
        var call = CallInState(channel, CallState.Dialing, CallState.Ringing, CallState.Connected);

        await call.SendDtmfAsync(DtmfTone.FromCode(1));

        Assert.Equal(new byte[] { 1 }, channel.SentCodes);
    }

    [Fact]
    public async Task SendDtmf_in_OnHold_is_allowed_and_reaches_the_channel()
    {
        var channel = new FakeDtmfChannel();
        var call = CallInState(
            channel, CallState.Dialing, CallState.Ringing, CallState.Connected, CallState.OnHold);

        await call.SendDtmfAsync(DtmfTone.FromCode(9));

        Assert.Equal(new byte[] { 9 }, channel.SentCodes);
    }

    [Theory]
    [InlineData(CallState.Idle)]
    [InlineData(CallState.Dialing)]
    public async Task SendDtmf_before_the_dialog_can_carry_media_is_rejected(CallState state)
    {
        var channel = new FakeDtmfChannel();
        // Idle is the initial state; Dialing is reached directly from Idle.
        var call = state == CallState.Idle
            ? CallInState(channel)
            : CallInState(channel, CallState.Dialing);
        Assert.Equal(state, call.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => call.SendDtmfAsync(DtmfTone.FromCode(2)));

        Assert.Empty(channel.SentCodes); // guard fired before the channel send ran
    }

    [Fact]
    public async Task SendDtmf_while_transferring_is_rejected()
    {
        var channel = new FakeDtmfChannel();
        var call = CallInState(
            channel, CallState.Dialing, CallState.Ringing, CallState.Connected, CallState.Transferring);
        Assert.Equal(CallState.Transferring, call.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => call.SendDtmfAsync(DtmfTone.FromCode(3)));

        Assert.Empty(channel.SentCodes);
    }

    // Minimal channel: records the DTMF codes routed to it; everything else is inert.
    private sealed class FakeDtmfChannel : ICallChannel
    {
        private readonly List<byte> _sent = [];
        public IReadOnlyList<byte> SentCodes => _sent;

        public Task SendDtmfAsync(byte dtmfCode) { _sent.Add(dtmfCode); return Task.CompletedTask; }

        public void BindCallbacks(CallChannelCallbacks callbacks) { }
        public void Dispose() { }

        public Task AnswerAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HangupAsync() => Task.CompletedTask;
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
