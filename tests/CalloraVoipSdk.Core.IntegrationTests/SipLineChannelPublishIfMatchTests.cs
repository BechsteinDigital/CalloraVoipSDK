using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-066b Slice 4 — adapter hop: <see cref="SipLineChannel.PublishAsync"/> maps a non-blank
/// <c>ifMatch</c> onto the outbound <see cref="SipPublishRequest.IfMatch"/> (RFC 3903 SIP-If-Match),
/// and leaves it null for an initial publication.
/// </summary>
public sealed class SipLineChannelPublishIfMatchTests
{
    private static SipLineChannel Channel(CapturingSig sig) =>
        new(new SipAccount { Username = "u", Password = "p", SipServer = "s" },
            "test/1.0", new NoopReg(), sig, new NoopSdp(), iceAgent: null,
            SrtpPolicy.Disabled, telemetry: null, NullLoggerFactory.Instance,
            preferredCodecNames: null, dtlsOptions: null, offerDtlsSrtp: false,
            enableVideo: false, preferredVideoCodecNames: null, requireSecureSignalingForSdes: false);

    [Fact]
    public async Task Publish_with_an_if_match_maps_it_onto_the_request()
    {
        var sig = new CapturingSig();
        using var line = Channel(sig);

        var result = await line.PublishAsync("presence", string.Empty, "text/plain", 1800, ifMatch: "etag-old");

        Assert.Equal("etag-old", sig.LastRequest!.IfMatch);
        Assert.Equal(1800, sig.LastRequest.ExpiresSeconds);
        Assert.Equal("etag-new", result.ETag);
    }

    [Fact]
    public async Task Publish_without_an_if_match_leaves_the_request_if_match_null()
    {
        var sig = new CapturingSig();
        using var line = Channel(sig);

        await line.PublishAsync("presence", "<presence/>", "application/pidf+xml", 3600);

        Assert.Null(sig.LastRequest!.IfMatch);
    }

    [Fact]
    public async Task Publish_with_a_whitespace_if_match_is_normalized_to_null()
    {
        var sig = new CapturingSig();
        using var line = Channel(sig);

        await line.PublishAsync("presence", "<presence/>", "application/pidf+xml", 3600, ifMatch: "   ");

        Assert.Null(sig.LastRequest!.IfMatch);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class CapturingSig : ISipCallSignalingService
    {
        public SipPublishRequest? LastRequest { get; private set; }

        public event EventHandler<SipIncomingInviteEventArgs>? IncomingInvite { add { } remove { } }
        public event EventHandler<SipIncomingMessageEventArgs>? IncomingMessage { add { } remove { } }
        public event EventHandler<SipIncomingInviteEventArgs>? OutboundCallStarted { add { } remove { } }
        public Task<ISipCallSession> InviteAsync(SipInviteRequest request, Action<ISipCallSession>? onSessionCreated = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipSubscriptionHandle> SubscribeAsync(SipSubscribeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> SendMessageAsync(SipMessageRequest request, CancellationToken ct = default) => Task.FromResult(200);

        public Task<SipPublishResult> PublishAsync(SipPublishRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new SipPublishResult(200, "etag-new", request.ExpiresSeconds));
        }

        public void Dispose() { }
    }

    private sealed class NoopReg : ISipRegistrationService
    {
        public Task<SipRegistrationResult> RegisterAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> UnregisterAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> UnregisterAllAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SipRegistrationResult> FetchBindingsAsync(SipRegistrationRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoopSdp : ISdpNegotiator
    {
        public string BuildDefaultSdp(IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? options = null) => "v=0";
        public string? TryBuildNegotiatedAnswer(string remoteOffer, IPEndPoint localEndPoint, bool hold, SdpMediaNegotiationOptions? localOptions = null) => null;
        public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint) => null;
        public bool IsRemoteHoldSdp(string? sdp) => false;
    }
}
