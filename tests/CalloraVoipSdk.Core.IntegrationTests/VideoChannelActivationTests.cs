using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video channel activation (WebRTC phase 2b slice 3): with <c>EnableVideo</c> the
/// production <see cref="SipCoreCallChannel"/> reserves a video port, offers/answers
/// <c>m=video</c>, and publishes video-bearing <see cref="CallMediaParameters"/> — the
/// path that makes video reachable from real SIP calls, not just hand-built parameters.
/// </summary>
public sealed class VideoChannelActivationTests
{
    [Fact]
    public async Task Video_enabled_channel_answers_inbound_video_offer_and_publishes_video_params()
    {
        using var channel = CreateChannel(enableVideo: true);
        var (parameters, answerSdp) = await AnswerAsync(channel, AudioVideoOffer());

        // The answer carries a real m=video line on the channel's reserved port…
        Assert.Contains("m=video ", answerSdp, StringComparison.Ordinal);
        Assert.DoesNotContain("m=video 0 ", answerSdp, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:96 VP8/90000", answerSdp, StringComparison.Ordinal);

        // …and the published media parameters carry the negotiated video sub-stream.
        Assert.NotNull(parameters.Video);
        Assert.Equal("VP8", parameters.Video!.CodecName);
        Assert.Equal(96, parameters.Video.PayloadType);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5004), parameters.Video.RemoteEndPoint);
    }

    [Fact]
    public async Task Video_enabled_channel_offers_m_video_on_the_outbound_path()
    {
        // A non-SDES policy: an SDES a=crypto offer suppresses video (SDES-keyed video is
        // not wired), so outbound video needs either a plain (Disabled) or DTLS offer.
        using var channel = CreateChannel(enableVideo: true, policy: SrtpPolicy.Disabled);
        var offer = await channel.BuildOfferSdpAsync(
            new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort), hold: false, CancellationToken.None);

        Assert.Contains("m=video ", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("m=video 0 ", offer, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:96 VP8/90000", offer, StringComparison.Ordinal);
        Assert.True(offer.IndexOf("m=audio", StringComparison.Ordinal)
                    < offer.IndexOf("m=video", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sdes_offer_suppresses_video_on_the_outbound_path()
    {
        // Documents the interaction: EnableVideo + a SDES-offering policy stays audio-only
        // outbound (SDES-keyed video fail-closed).
        using var channel = CreateChannel(enableVideo: true, policy: SrtpPolicy.Optional);
        var offer = await channel.BuildOfferSdpAsync(
            new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort), hold: false, CancellationToken.None);

        Assert.Contains("a=crypto:", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("m=video", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Video_disabled_channel_omits_m_video_on_the_outbound_path()
    {
        using var channel = CreateChannel(enableVideo: false, policy: SrtpPolicy.Disabled);
        var offer = await channel.BuildOfferSdpAsync(
            new IPEndPoint(IPAddress.Loopback, channel.LocalMediaPort), hold: false, CancellationToken.None);

        Assert.DoesNotContain("m=video", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Video_disabled_channel_declines_video_and_stays_audio_only()
    {
        using var channel = CreateChannel(enableVideo: false);
        var (parameters, answerSdp) = await AnswerAsync(channel, AudioVideoOffer());

        // RFC 3264 §6: the offered video m-line is still mirrored, but declined (port 0).
        Assert.Contains("m=video 0 ", answerSdp, StringComparison.Ordinal);
        Assert.Null(parameters.Video);
    }

    [Fact]
    public async Task Video_enabled_channel_reserves_a_distinct_video_port()
    {
        using var channel = CreateChannel(enableVideo: true);
        var (parameters, _) = await AnswerAsync(channel, AudioVideoOffer());

        // Audio and video bind different local ports.
        Assert.NotNull(parameters.Video);
        Assert.NotEqual(parameters.LocalEndPoint.Port, parameters.Video!.LocalEndPoint.Port);
        Assert.Equal(channel.LocalMediaPort, parameters.LocalEndPoint.Port);
    }

    [Fact]
    public async Task Audio_only_offer_to_video_channel_yields_no_video_params()
    {
        using var channel = CreateChannel(enableVideo: true);
        var (parameters, answerSdp) = await AnswerAsync(channel, AudioOnlyOffer());

        Assert.DoesNotContain("m=video", answerSdp, StringComparison.Ordinal);
        Assert.Null(parameters.Video);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SipCoreCallChannel CreateChannel(
        bool enableVideo, SrtpPolicy policy = SrtpPolicy.Optional) => new(
        NullLogger<SipCoreCallChannel>.Instance,
        new SdpNegotiator(),
        NullSipTelemetrySink.Instance,
        policy,
        policySource: "test",
        iceAgent: null,
        preferredCodecNames: null,
        advertisedPublicMediaAddress: null,
        dtlsOptions: null,
        offerDtlsSrtp: false,
        enableVideo: enableVideo);

    private static async Task<(CallMediaParameters Parameters, string AnswerSdp)> AnswerAsync(
        SipCoreCallChannel channel, string offerSdp)
    {
        var session = new FakeInboundVideoSession(offerSdp);
        CallMediaParameters? fired = null;
        channel.MediaParametersNegotiated += (_, p) => fired = p;

        channel.AttachSession(session);
        await channel.AnswerAsync(CancellationToken.None);

        Assert.NotNull(fired);
        Assert.NotNull(session.CapturedAnswerSdp);
        return (fired!, session.CapturedAnswerSdp!);
    }

    private static string AudioOnlyOffer() =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n";

    private static string AudioVideoOffer() =>
        AudioOnlyOffer() + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

    private sealed class FakeInboundVideoSession(string remoteSdp) : ISipCallSession
    {
        public string? CapturedAnswerSdp { get; private set; }

        public string CallId => "e2e-video-call";
        public string LocalUri => "sip:sdk@127.0.0.1";
        public string RemoteUri => "sip:peer@127.0.0.1";
        public SipDialogState State { get; private set; } = SipDialogState.Ringing;
        public SipDialogTerminationReason? LastTerminationReason => null;
        public bool IsInbound => true;
        public string? RemoteAssertedIdentity => null;
        public string? RemoteSdp => remoteSdp;
        public string? LocalSdp => CapturedAnswerSdp;
        public IPEndPoint LocalSignalingEndPoint => new(IPAddress.Loopback, 5060);
        public IPEndPoint? RemoteSignalingEndPoint => new(IPAddress.Loopback, 5060);

        public event EventHandler<SipDialogStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<bool>? RemoteHoldChanged { add { } remove { } }
        public event EventHandler<SipDtmfReceivedEventArgs>? DtmfReceived { add { } remove { } }
        public event EventHandler<SipTransferRequestedEventArgs>? TransferRequested { add { } remove { } }
        public event EventHandler<SipSubscriptionRequestedEventArgs>? SubscriptionRequested { add { } remove { } }
        public event EventHandler<SipNotifyReceivedEventArgs>? NotifyReceived { add { } remove { } }

        public Task AnswerAsync(string? sessionDescription = null, CancellationToken ct = default)
        {
            CapturedAnswerSdp = sessionDescription;
            State = SipDialogState.Established;
            return Task.CompletedTask;
        }

        public Task RejectAsync(int statusCode = 486, string? reasonPhrase = null, CancellationToken ct = default)
            => throw new InvalidOperationException($"E2E test peer rejected with {statusCode}.");

        public Task HangupAsync(CancellationToken ct = default, SipDialogTerminationReason? reason = null)
            => Task.CompletedTask;

        public Task RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task HoldAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnholdAsync(string? sessionDescription = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendDtmfAsync(char digit, int durationMs = 160, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendInfoAsync(string contentType, string body, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendReferAsync(string referTo, string? referredBy = null, bool suppressSubscription = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendOptionsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendSubscribeAsync(string eventType, int expiresSeconds = 300, string? acceptHeader = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SendNotifyAsync(string eventType, string subscriptionState, string? contentType = null, string? body = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
