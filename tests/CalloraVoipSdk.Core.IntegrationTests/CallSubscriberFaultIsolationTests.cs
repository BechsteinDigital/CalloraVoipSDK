using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A throwing event subscriber must not take internal cleanup with it (#165 P2-5). K3 says a handler must not
/// throw — but when one does, the call's channel still has to be disposed and the terminated call still has to
/// leave the registry, or the SDK is left with a call that is terminated on paper while its signaling channel
/// lives on and it keeps showing up in <c>Find</c>/<c>Active</c> forever.
/// </summary>
public sealed class CallSubscriberFaultIsolationTests
{
    private static (CallManager Registry, ICall Call, DisposeTrackingCallChannel Channel) InboundCall()
    {
        var registry = new CallManager();
        var lineChannel = new InboundCapturingLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", SipServer = "s" },
            lineChannel, registry, maxCalls: 0, NullLoggerFactory.Instance);

        ICall? inbound = null;
        line.IncomingCall += (_, e) => inbound = e.Call;

        var channel = new DisposeTrackingCallChannel();
        lineChannel.RaiseInbound(channel, "sip:caller@remote.invalid");

        return (registry, inbound!, channel);
    }

    [Fact]
    public async Task A_throwing_state_subscriber_does_not_keep_the_call_channel_alive()
    {
        var (_, call, channel) = InboundCall();
        call.StateChanged += (_, e) =>
        {
            if (e.NewState == CallState.Terminated)
                throw new InvalidOperationException("subscriber fault");
        };

        await call.HangupAsync();

        Assert.Equal(CallState.Terminated, call.State);
        Assert.True(channel.Disposed, "the call channel must be disposed even when a subscriber threw");
    }

    [Fact]
    public async Task A_throwing_aggregate_subscriber_does_not_keep_the_call_registered()
    {
        var (registry, call, _) = InboundCall();
        ((ICallManager)registry).CallStateChanged += (_, _) => throw new InvalidOperationException("subscriber fault");

        var removed = 0;
        ((ICallManager)registry).CallRemoved += (_, _) => removed++;

        await call.HangupAsync();

        Assert.Null(registry.Find(call.CallId));
        Assert.Empty(registry.Active);
        Assert.Equal(1, removed); // the removal notification still goes out
    }

    [Fact]
    public async Task A_throwing_removal_subscriber_still_leaves_the_registry_consistent()
    {
        var (registry, call, channel) = InboundCall();
        ((ICallManager)registry).CallRemoved += (_, _) => throw new InvalidOperationException("subscriber fault");

        await call.HangupAsync();

        Assert.Null(registry.Find(call.CallId));
        Assert.True(channel.Disposed);
    }

    private sealed class DisposeTrackingCallChannel : ICallChannel
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public void BindCallbacks(CallChannelCallbacks callbacks) { }
        public Task HangupAsync() => Task.CompletedTask;
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
