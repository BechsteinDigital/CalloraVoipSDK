using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Public ICE-restart API on the <see cref="Call"/> aggregate (#62 Punkt 2): <c>RestartIceAsync</c>
/// requires the <see cref="CallState.Connected"/> precondition and delegates to the channel without a
/// state transition (the restart re-negotiates only the ICE transport, the call stays Connected).
/// </summary>
public sealed class CallIceRestartTests
{
    private static Call ConnectedCall(RecordingRestartChannel channel)
    {
        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        call.TransitionTo(CallState.Ringing);
        call.TransitionTo(CallState.Connected);
        return call;
    }

    [Fact]
    public async Task RestartIce_when_connected_delegates_to_the_channel_and_stays_connected()
    {
        var channel = new RecordingRestartChannel();
        var call = ConnectedCall(channel);

        await call.RestartIceAsync();

        Assert.Equal(1, channel.RestartCount);
        Assert.Equal(CallState.Connected, call.State); // no transition — only the ICE transport re-negotiates
    }

    [Fact]
    public async Task RestartIce_from_a_non_connected_state_is_rejected_without_running_the_channel()
    {
        var channel = new RecordingRestartChannel();
        var call = new Call(
            CallId.New(), CallDirection.Inbound, "sip:remote@test.invalid",
            channel, line: null!, NullLogger<Call>.Instance);
        call.TransitionTo(CallState.Ringing); // not Connected

        await Assert.ThrowsAsync<InvalidOperationException>(() => call.RestartIceAsync());

        Assert.Equal(CallState.Ringing, call.State);
        Assert.Equal(0, channel.RestartCount); // guard fired before the channel ran
    }

    // Minimal channel that records RestartIceAsync; everything else is inert.
    private sealed class RecordingRestartChannel : ICallChannel
    {
        public int RestartCount { get; private set; }

        public Task RestartIceAsync()
        {
            RestartCount++;
            return Task.CompletedTask;
        }

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

#pragma warning disable CS0067 // no media negotiation in these restart tests
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }
}
