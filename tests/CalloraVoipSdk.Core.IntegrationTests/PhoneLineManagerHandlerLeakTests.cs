using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #17.9 regression: <see cref="PhoneLineManager"/> must detach the aggregate IncomingCall/IncomingMessage
/// forwarding handlers it wires onto each line when the line is unregistered or the manager is disposed —
/// otherwise a still-live line would keep firing the manager's aggregate events after unregister (handler leak).
/// </summary>
public sealed class PhoneLineManagerHandlerLeakTests
{
    [Fact]
    public async Task Unregister_detaches_the_aggregate_incoming_call_handler()
    {
        ManagerHarnessLineChannel? captured = null;
        var manager = new PhoneLineManager(account =>
        {
            captured = new ManagerHarnessLineChannel();
            return new PhoneLine(account, captured, new NoopCallRegistry(), maxCalls: 0, NullLoggerFactory.Instance);
        });

        var aggregateIncoming = 0;
        manager.IncomingCall += (_, _) => aggregateIncoming++;

        var line = manager.Register(new SipAccount { Username = "u", SipServer = "s" });
        Assert.NotNull(captured);

        // Sanity: while registered, an inbound call reaches the aggregate handler.
        captured!.RaiseInbound(new ManagerInertCallChannel(), "sip:a@remote.invalid");
        Assert.Equal(1, aggregateIncoming);

        await manager.UnregisterAsync(line.LineId);

        // After unregister the line object is still reachable via the captured channel: a further inbound must
        // NOT reach the manager's aggregate handler, because Register's forwarding delegate was detached.
        captured.RaiseInbound(new ManagerInertCallChannel(), "sip:b@remote.invalid");
        Assert.Equal(1, aggregateIncoming);
    }

    [Fact]
    public void Dispose_detaches_the_aggregate_incoming_call_handler()
    {
        ManagerHarnessLineChannel? captured = null;
        var manager = new PhoneLineManager(account =>
        {
            captured = new ManagerHarnessLineChannel();
            return new PhoneLine(account, captured, new NoopCallRegistry(), maxCalls: 0, NullLoggerFactory.Instance);
        });

        var aggregateIncoming = 0;
        manager.IncomingCall += (_, _) => aggregateIncoming++;

        manager.Register(new SipAccount { Username = "u", SipServer = "s" });
        manager.Dispose();

        captured!.RaiseInbound(new ManagerInertCallChannel(), "sip:c@remote.invalid");
        Assert.Equal(0, aggregateIncoming);
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    private sealed class ManagerHarnessLineChannel : ILineChannel
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

    private sealed class ManagerInertCallChannel : ICallChannel
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

#pragma warning disable CS0067
        public event EventHandler<CallMediaParameters>? MediaParametersNegotiated;
#pragma warning restore CS0067
    }
}
