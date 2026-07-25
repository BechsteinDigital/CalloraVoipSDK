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
        Assert.Null(channel.LastIfMatch);
    }

    [Fact]
    public async Task RefreshPublicationAsync_forwards_empty_body_and_the_etag_as_if_match()
    {
        var channel = new CapturingLineChannel();
        var line = NewLine(channel);

        var result = await line.RefreshPublicationAsync("presence", "etag-in", 1200);

        Assert.Equal("etag-9", result.ETag);
        Assert.Equal("etag-in", channel.LastIfMatch);
        Assert.Equal("presence", channel.LastPublish.EventType);
        Assert.Equal(string.Empty, channel.LastPublish.Body);
        Assert.Equal(1200, channel.LastPublish.Expires);
    }

    [Fact]
    public async Task ModifyPublicationAsync_forwards_body_content_type_expires_and_the_etag_as_if_match()
    {
        var channel = new CapturingLineChannel();
        var line = NewLine(channel);

        var result = await line.ModifyPublicationAsync("presence", "etag-in", "<presence/>", "application/pidf+xml", 900);

        Assert.Equal("etag-9", result.ETag);
        Assert.Equal("etag-in", channel.LastIfMatch);
        Assert.Equal(("presence", "<presence/>", "application/pidf+xml", 900), channel.LastPublish);
    }

    [Fact]
    public async Task RemovePublicationAsync_forwards_expires_zero_empty_body_and_the_etag_as_if_match()
    {
        var channel = new CapturingLineChannel();
        var line = NewLine(channel);

        await line.RemovePublicationAsync("presence", "etag-in");

        Assert.Equal("etag-in", channel.LastIfMatch);
        Assert.Equal("presence", channel.LastPublish.EventType);
        Assert.Equal(string.Empty, channel.LastPublish.Body);
        Assert.Equal(0, channel.LastPublish.Expires);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Lifecycle_methods_reject_a_blank_etag(string blankEtag)
    {
        var line = NewLine(new CapturingLineChannel());

        await Assert.ThrowsAsync<ArgumentException>(() => line.RefreshPublicationAsync("presence", blankEtag));
        await Assert.ThrowsAsync<ArgumentException>(() => line.ModifyPublicationAsync("presence", blankEtag, "<presence/>"));
        await Assert.ThrowsAsync<ArgumentException>(() => line.RemovePublicationAsync("presence", blankEtag));
    }

    private static PhoneLine NewLine(ILineChannel channel) => new(
        new SipAccount { Username = "u", Password = "p", SipServer = "sipconnect.example" },
        channel,
        new NoopCallRegistry(),
        maxCalls: 0,
        NullLoggerFactory.Instance);

    private sealed class NoopCallRegistry : ICallRegistry
    {
        public void Register(Call call) { }
        public IReadOnlyCollection<ICall> Active => [];
    }

    private sealed class CapturingLineChannel : ILineChannel
    {
        public (string EventType, string Body, string ContentType, int Expires) LastPublish { get; private set; }
        public string? LastIfMatch { get; private set; }

        public Task<PublishResult> PublishAsync(string eventType, string body, string contentType, int expiresSeconds, string? ifMatch = null, CancellationToken ct = default)
        {
            LastPublish = (eventType, body, contentType, expiresSeconds);
            LastIfMatch = ifMatch;
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
