using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #17.8 regression: an inbound call's first <see cref="CallState.Idle"/> → <see cref="CallState.Ringing"/>
/// transition must be observable on the aggregate <see cref="CallManager.CallStateChanged"/> stream. The line
/// must register the call (which subscribes the aggregate relay) BEFORE it transitions to Ringing, and the
/// direct <see cref="IPhoneLine.IncomingCall"/> event must still fire exactly once.
/// </summary>
public sealed class InboundRingingAggregateStreamTests
{
    [Fact]
    public void Inbound_Idle_to_Ringing_reaches_the_aggregate_call_state_stream()
    {
        var registry = new CallManager();
        var channel = new InboundCapturingLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", SipServer = "s" },
            channel, registry, maxCalls: 0, NullLoggerFactory.Instance);

        var aggregate = new List<(CallState Old, CallState New)>();
        ((ICallManager)registry).CallStateChanged += (_, e) => aggregate.Add((e.OldState, e.NewState));

        var incomingCalls = new List<ICall>();
        line.IncomingCall += (_, e) => incomingCalls.Add(e.Call);

        // Drive an inbound INVITE through the captured inbound handler.
        channel.RaiseInbound(new InertCallChannel(), "sip:caller@remote.invalid");

        Assert.Contains((CallState.Idle, CallState.Ringing), aggregate);
        var call = Assert.Single(incomingCalls);
        Assert.Equal(CallState.Ringing, call.State);
    }

    // Captures the inbound handler the PhoneLine binds, so the test can raise an inbound INVITE.
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

    // Inert call channel: the inbound-ringing path never exercises it.
    private sealed class InertCallChannel : ICallChannel
    {
        public void BindCallbacks(CallChannelCallbacks callbacks) { }
        public void Dispose() { }
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
}
