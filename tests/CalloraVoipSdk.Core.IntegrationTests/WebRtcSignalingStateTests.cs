using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 8829 §4.1.3 signalling-state machine of the WebRTC peer (P3a): the offer/answer half of the
/// lifecycle, made explicit and observable on top of the existing (byte-identical) offer/answer flow.
/// Covers the offerer path (Stable → HaveLocalOffer → Stable), the answerer path
/// (Stable → HaveRemoteOffer → Stable, both transitions observable in one call), the Closed terminal, and
/// the invalid-transition guards. This is state observation + guards only — no renegotiation / re-offer apply
/// (the re-offer throw in SetRemoteDescription stays the P3b boundary).
/// </summary>
public sealed class WebRtcSignalingStateTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];
    private static readonly IReadOnlyList<SdpCodecDefinition> Opus =
        [new SdpCodecDefinition { PayloadType = 111, Name = "opus", ClockRate = 48000, Channels = 2 }];

    [Fact]
    public async Task A_fresh_peer_starts_in_stable()
    {
        await using var peer = Peer(Pcmu);
        Assert.Equal(WebRtcSignalingState.Stable, peer.SignalingState);
    }

    [Fact]
    public async Task CreateOffer_moves_stable_to_have_local_offer_and_fires_the_event()
    {
        await using var peer = Peer(Pcmu);
        var states = new List<WebRtcSignalingState>();
        peer.SignalingStateChanged += states.Add;

        peer.CreateOffer();

        Assert.Equal(WebRtcSignalingState.HaveLocalOffer, peer.SignalingState);
        Assert.Equal([WebRtcSignalingState.HaveLocalOffer], states);
    }

    [Fact]
    public async Task A_re_offer_before_any_answer_stays_in_have_local_offer_without_a_second_event()
    {
        // RFC 8829 §4.1.3: createOffer is idempotent in have-local-offer (a re-offer replaces the pending
        // offer, no state change). This is the same-peer re-offer the msid-stability test relies on.
        await using var peer = Peer(Pcmu);
        var states = new List<WebRtcSignalingState>();
        peer.SignalingStateChanged += states.Add;

        peer.CreateOffer();
        peer.CreateOffer();

        Assert.Equal(WebRtcSignalingState.HaveLocalOffer, peer.SignalingState);
        Assert.Equal([WebRtcSignalingState.HaveLocalOffer], states); // exactly one transition
    }

    [Fact]
    public async Task Offerer_applying_the_answer_returns_to_stable()
    {
        await using var offerer = Peer(Pcmu);
        await using var answerer = Peer(Pcmu);
        var states = new List<WebRtcSignalingState>();
        offerer.SignalingStateChanged += states.Add;

        var offer = offerer.CreateOffer();
        var answer = await answerer.SetRemoteDescriptionAsync(offer);
        await offerer.SetRemoteDescriptionAsync(answer);

        Assert.Equal(WebRtcSignalingState.Stable, offerer.SignalingState);
        // HaveLocalOffer (createOffer) then back to Stable (answer applied).
        Assert.Equal([WebRtcSignalingState.HaveLocalOffer, WebRtcSignalingState.Stable], states);
    }

    [Fact]
    public async Task Answerer_applying_an_offer_passes_through_have_remote_offer_back_to_stable()
    {
        await using var answerer = Peer(Pcmu);
        var states = new List<WebRtcSignalingState>();
        answerer.SignalingStateChanged += states.Add;

        await answerer.SetRemoteDescriptionAsync(WebRtcOffer());

        Assert.Equal(WebRtcSignalingState.Stable, answerer.SignalingState);
        // W3C two-transition answerer path: both events fire within the single SetRemoteDescription call.
        Assert.Equal([WebRtcSignalingState.HaveRemoteOffer, WebRtcSignalingState.Stable], states);
    }

    [Fact]
    public async Task CreateOffer_in_have_local_offer_is_allowed_but_creating_after_close_throws()
    {
        var peer = Peer(Pcmu);
        peer.CreateOffer();                 // Stable → HaveLocalOffer
        peer.CreateOffer();                 // idempotent re-offer, still valid
        await peer.DisposeAsync();          // → Closed

        // RFC 8829 §4.1.3: an offer cannot be produced from Closed.
        Assert.Throws<InvalidOperationException>(() => peer.CreateOffer());
    }

    [Fact]
    public async Task An_answerer_that_cannot_negotiate_stays_out_of_stable()
    {
        // The peer only offers Opus; the remote offers PCMU — no intersection. The answerer entered
        // HaveRemoteOffer, and a failed answer does not roll signalling back to Stable (mirrors W3C).
        await using var peer = Peer(Opus);

        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SetRemoteDescriptionAsync(WebRtcOffer()));

        Assert.Equal(WebRtcSignalingState.HaveRemoteOffer, peer.SignalingState);
        Assert.Equal(WebRtcConnectionState.Failed, peer.State);
    }

    [Fact]
    public async Task Disposing_the_peer_moves_signalling_to_closed_and_fires_once()
    {
        var peer = Peer(Pcmu);
        var states = new List<WebRtcSignalingState>();
        peer.SignalingStateChanged += states.Add;

        await peer.DisposeAsync();
        await peer.DisposeAsync(); // idempotent — no second Closed event

        Assert.Equal(WebRtcSignalingState.Closed, peer.SignalingState);
        Assert.Equal([WebRtcSignalingState.Closed], states);
    }

    [Fact]
    public async Task A_second_set_remote_description_renegotiates_and_returns_to_stable()
    {
        // P3b-3: once a session exists (Stable after a completed answerer cycle), a second SetRemoteDescription is
        // renegotiation — it applies the track diff to the live session and runs the answerer's two transitions
        // (Stable → HaveRemoteOffer → Stable) again, ending back in Stable rather than throwing the old boundary.
        await using var peer = Peer(Pcmu);
        await peer.SetRemoteDescriptionAsync(WebRtcOffer());
        var states = new List<WebRtcSignalingState>();
        peer.SignalingStateChanged += states.Add;

        var answer = await peer.SetRemoteDescriptionAsync(WebRtcOffer());

        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.Equal([WebRtcSignalingState.HaveRemoteOffer, WebRtcSignalingState.Stable], states);
        Assert.Equal(WebRtcSignalingState.Stable, peer.SignalingState);
    }

    private static WebRtcPeerConnection Peer(IReadOnlyList<SdpCodecDefinition> audioCodecs) =>
        new(
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = audioCodecs,
                VideoTracks =
                [
                    new SdpVideoMediaOptions
                    {
                        Port = 6002,
                        Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                    },
                ],
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance);

    private static string WebRtcOffer() => new SdpSessionSerializer().Serialize(
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
                Video = new SdpVideoMediaOptions
                {
                    Port = 5002,
                    Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                },
            }));
}
