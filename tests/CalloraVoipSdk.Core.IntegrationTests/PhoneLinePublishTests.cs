using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Messages;
using CalloraVoipSdk.Core.Domain.Publications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-066b Slice 2 — domain hop: <see cref="PhoneLine.PublishAsync"/> forwards a SIP PUBLISH to the line
/// channel with the event/body/content-type/expires and returns the channel's <see cref="PublishResult"/>
/// (the SIP-ETag + granted lifetime) to the caller.
/// </summary>
public sealed class PhoneLinePublishTests
{
    [Fact]
    public async Task PublishAsync_forwards_to_the_channel_and_returns_its_result()
    {
        var channel = new CapturingLineChannel();
        var line = new PhoneLine(
            new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
            channel,
            new NoopCallRegistry(),
            maxCalls: 0,
            NullLoggerFactory.Instance);

        var result = await line.PublishAsync("presence", "<presence/>", "application/pidf+xml", 1800);

        Assert.Equal("etag-9", result.ETag);
        Assert.Equal(1800, result.ExpiresSeconds);
        Assert.Equal(("presence", "<presence/>", "application/pidf+xml", 1800), channel.LastPublish);
    }

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    private sealed class CapturingLineChannel : ILineChannel
    {
        public (string EventType, string Body, string ContentType, int Expires) LastPublish { get; private set; }

        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, CancellationToken ct = default)
        {
            LastPublish = (eventType, body, contentType, expiresSeconds);
            return Task.FromResult(new PublishResult("etag-9", expiresSeconds));
        }

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
        public void SetMessageHandler(Action<SipInstantMessage> onMessage) { }
        public Task SendMessageAsync(string targetUri, string body, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
