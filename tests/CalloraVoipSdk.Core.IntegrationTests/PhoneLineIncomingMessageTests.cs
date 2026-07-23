using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-066a Slice 2 — domain hop: an inbound SIP MESSAGE delivered by the line channel is surfaced on
/// <see cref="PhoneLine.IncomingMessage"/> without creating a call (RFC 3428; MESSAGE is stateless).
/// </summary>
public sealed class PhoneLineIncomingMessageTests
{
    [Fact]
    public void A_channel_delivered_MESSAGE_is_surfaced_on_IncomingMessage()
    {
        var channel = new CapturingLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new NoopCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);

        SipInstantMessage? received = null;
        line.IncomingMessage += (_, e) => received = e.Message;

        var message = new SipInstantMessage(
            "<sip:alice@example.test>", "<sip:u@sipconnect.example>", "hi", "text/plain", "call-1");
        channel.DeliverMessage(message);

        Assert.Same(message, received);
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    // A line-channel test double that captures the message handler PhoneLine wires, so the test can
    // deliver an inbound MESSAGE as the real SipLineChannel would after answering it 200 OK.
    private sealed class CapturingLineChannel : ILineChannel
    {
        private Action<SipInstantMessage>? _onMessage;

        public void DeliverMessage(SipInstantMessage message) => _onMessage?.Invoke(message);

        public void StartRegistration(
            Action<LineState> onStateChange,
            Action<int>? onReconnecting = null,
            Action<ReregisterFailReason, int>? onReconnectFailed = null)
        { }

        public void StopRegistration() { }
        public Task StopRegistrationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ICallChannel PrepareOutboundChannel(DialOptions options) => throw new NotSupportedException();
        public Task StartOutboundDialAsync(ICallChannel channel, string targetUri, DialOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
        public void SetInboundHandler(Action<ICallChannel, string> onInbound) { }
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) => _onMessage = onMessage;
        public void Dispose() { }
    }
}
