using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The signalling surface of the WebRTC peer (Weg 1 / ADR-010 founder architecture): it consumes a
/// remote SDP offer and produces a WebRTC answer — BUNDLE (RFC 8843), DTLS-SRTP (RFC 5763), rtcp-mux
/// (RFC 8834), and the sdes:mid extension (RFC 9143) — via the existing SDP negotiator, driving the
/// <see cref="WebRtcConnectionState"/> machine. It is signalling-neutral (SDP in, SDP out) and does not
/// touch the SIP call path.
/// </summary>
public sealed class WebRtcPeerConnectionTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];
    private static readonly IReadOnlyList<SdpCodecDefinition> Opus =
        [new SdpCodecDefinition { PayloadType = 111, Name = "opus", ClockRate = 48000, Channels = 2 }];

    [Fact]
    public async Task SetRemoteDescription_answers_a_webrtc_offer_with_bundle_dtls_and_mid()
    {
        await using var peer = Peer(Pcmu);
        var states = new List<WebRtcConnectionState>();
        peer.ConnectionStateChanged += states.Add;

        var answer = await peer.SetRemoteDescriptionAsync(WebRtcOffer());

        Assert.Equal(WebRtcConnectionState.Connecting, peer.State);
        Assert.Contains(WebRtcConnectionState.Connecting, states);
        Assert.Equal(answer, peer.LocalDescription);

        var parsed = new SdpSessionParser().Parse(answer);
        Assert.StartsWith("BUNDLE", parsed.Group);

        var audio = parsed.Media.Single(m => m.MediaType == "audio");
        var video = parsed.Media.Single(m => m.MediaType == "video");
        Assert.Equal("audio", audio.Mid);
        Assert.Equal("video", video.Mid);
        Assert.Contains(audio.Extensions, e => e.Uri == RtpHeaderExtensionUris.Mid);
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Mid);
        Assert.NotNull(audio.Fingerprint);         // DTLS-SRTP answer (RFC 5763)
        Assert.NotNull(audio.IceUfrag);            // ICE credentials (RFC 8839)
        Assert.True(audio.RtcpMux);                // rtcp-mux (RFC 8834)
    }

    [Fact]
    public async Task Offerer_applying_the_answer_returns_its_own_offer_as_local_description()
    {
        // Guards the offerer branch of SetRemoteDescription (HARD-C6): the local description belongs to
        // the pending offer and is captured under _sync, so applying the peer's answer returns the
        // original offer unchanged rather than a stale/torn read.
        await using var offerer = Peer(Pcmu);
        await using var answerer = Peer(Pcmu);

        var offer = offerer.CreateOffer();
        var answer = await answerer.SetRemoteDescriptionAsync(offer);

        var returnedLocal = await offerer.SetRemoteDescriptionAsync(answer);

        Assert.Equal(offer, returnedLocal);
        Assert.Equal(offer, offerer.LocalDescription);
        Assert.Equal(WebRtcConnectionState.Connecting, offerer.State);
    }

    [Fact]
    public async Task An_empty_remote_description_is_rejected()
    {
        await using var peer = Peer(Pcmu);
        await Assert.ThrowsAsync<ArgumentException>(() => peer.SetRemoteDescriptionAsync("   "));
        Assert.Equal(WebRtcConnectionState.New, peer.State);
    }

    [Fact]
    public async Task A_non_negotiable_offer_moves_the_peer_to_failed()
    {
        // The peer only offers Opus; the remote offers PCMU — no audio codec intersects.
        await using var peer = Peer(Opus);

        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SetRemoteDescriptionAsync(WebRtcOffer()));

        Assert.Equal(WebRtcConnectionState.Failed, peer.State);
        Assert.Null(peer.LocalDescription);
    }

    [Fact]
    public async Task Sending_after_dispose_throws_object_disposed()
    {
        var peer = Peer(Pcmu);
        await peer.SetRemoteDescriptionAsync(WebRtcOffer());
        await peer.DisposeAsync();

        // HARD-C6: a send begun after dispose is refused, never operating on a disposed media session.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await peer.SendAudioAsync(new byte[] { 1 }));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await peer.SendVideoFrameAsync(new byte[] { 1 }, 0));
    }

    [Fact]
    public async Task Disposing_twice_is_idempotent()
    {
        var peer = Peer(Pcmu);
        await peer.SetRemoteDescriptionAsync(WebRtcOffer());

        await peer.DisposeAsync();
        await peer.DisposeAsync();   // second dispose is a no-op (null session, drained gate)

        Assert.Equal(WebRtcConnectionState.Closed, peer.State);
    }

    [Fact]
    public async Task Disposing_the_peer_closes_it()
    {
        var closed = new List<WebRtcConnectionState>();
        var peer = Peer(Pcmu);
        peer.ConnectionStateChanged += closed.Add;

        await peer.DisposeAsync();

        Assert.Equal(WebRtcConnectionState.Closed, peer.State);
        Assert.Contains(WebRtcConnectionState.Closed, closed);
    }

    [Fact]
    public async Task SetRemoteDescription_builds_a_bound_media_transport()
    {
        await using var peer = Peer(Pcmu);

        await peer.SetRemoteDescriptionAsync(WebRtcOffer());

        Assert.NotNull(peer.LocalMediaEndPoint);
        Assert.NotEqual(0, peer.LocalMediaEndPoint!.Port); // the shared bundle socket bound
    }

    [Fact]
    public async Task StartAsync_before_a_remote_description_throws()
    {
        await using var peer = Peer(Pcmu);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.StartAsync());
    }

    [Fact]
    public async Task StartAsync_after_a_remote_description_starts_the_transport()
    {
        await using var peer = Peer(Pcmu);
        await peer.SetRemoteDescriptionAsync(WebRtcOffer());

        await peer.StartAsync(); // receive loop + ICE + DTLS handshake begin; no peer, so no Connected yet
    }

    [Fact]
    public void CreateOffer_advertises_a_host_ice_candidate_for_the_local_endpoint()
    {
        var offer = PeerAt(40100).CreateOffer();

        var audio = new SdpSessionParser().Parse(offer).Media.Single(m => m.MediaType == "audio");
        var candidate = Assert.Single(audio.Candidates.Where(c => c.Type == "host"));
        Assert.Equal("udp", candidate.Transport);
        Assert.Equal(1, candidate.Component); // RTP; rtcp-mux shares it
        Assert.Equal("127.0.0.1", candidate.Address);
        Assert.Equal(40100, candidate.Port);
    }

    [Fact]
    public void A_peer_without_a_fixed_port_advertises_no_host_candidate()
    {
        var offer = PeerAt(0).CreateOffer(); // ephemeral bind — the real port is unknown until the transport binds

        var audio = new SdpSessionParser().Parse(offer).Media.Single(m => m.MediaType == "audio");
        Assert.DoesNotContain(audio.Candidates, c => c.Type == "host");
    }

    [Fact]
    public async Task CreateOffer_stamps_a_stable_msid_on_audio_and_video()
    {
        await using var peer = Peer(Pcmu);

        var first = new SdpSessionParser().Parse(peer.CreateOffer());
        var audio = first.Media.Single(m => m.MediaType == "audio");
        var video = first.Media.Single(m => m.MediaType == "video");

        Assert.NotNull(audio.Msid);
        Assert.NotNull(video.Msid);
        Assert.Equal(audio.Msid!.StreamId, video.Msid!.StreamId);   // one MediaStream (RFC 8830)
        Assert.NotEqual(audio.Msid.TrackId, video.Msid.TrackId);     // distinct tracks

        // Track identity is stable across re-offers (same peer → same stream/track ids).
        var second = new SdpSessionParser().Parse(peer.CreateOffer());
        Assert.Equal(audio.Msid, second.Media.Single(m => m.MediaType == "audio").Msid);
        Assert.Equal(video.Msid, second.Media.Single(m => m.MediaType == "video").Msid);
    }

    [Fact]
    public async Task The_answer_carries_our_msid_on_audio_and_video()
    {
        await using var peer = Peer(Pcmu);

        var answer = new SdpSessionParser().Parse(await peer.SetRemoteDescriptionAsync(WebRtcOffer()));

        var audio = answer.Media.Single(m => m.MediaType == "audio");
        var video = answer.Media.Single(m => m.MediaType == "video");
        Assert.NotNull(audio.Msid);
        Assert.NotNull(video.Msid);
        Assert.Equal(audio.Msid!.StreamId, video.Msid!.StreamId);   // our one MediaStream
        Assert.NotEqual(audio.Msid.TrackId, video.Msid.TrackId);
    }

    [Fact]
    public async Task SetRemoteDescription_captures_the_remote_track_identity_grouped_by_stream()
    {
        // The offerer advertises one MediaStream carrying its audio and video tracks (a=msid); the answerer
        // must retain that remote identity so a receiver can group the tracks (W3C RTCTrackEvent.streams).
        await using var offerer = Peer(Pcmu);
        await using var answerer = Peer(Pcmu);

        await answerer.SetRemoteDescriptionAsync(offerer.CreateOffer());

        Assert.NotNull(answerer.RemoteAudioMsid);
        Assert.NotNull(answerer.RemoteVideoMsid);
        Assert.Equal(answerer.RemoteAudioMsid!.StreamId, answerer.RemoteVideoMsid!.StreamId);   // one remote stream
        Assert.NotEqual(answerer.RemoteAudioMsid.TrackId, answerer.RemoteVideoMsid.TrackId);
    }

    [Fact]
    public async Task SetRemoteDescription_leaves_the_remote_track_identity_null_when_the_offer_has_no_msid()
    {
        await using var answerer = Peer(Pcmu);

        await answerer.SetRemoteDescriptionAsync(WebRtcOffer());   // negotiator offer without a=msid

        Assert.Null(answerer.RemoteAudioMsid);
        Assert.Null(answerer.RemoteVideoMsid);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static WebRtcPeerConnection PeerAt(int localPort) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance);

    private static WebRtcPeerConnection Peer(IReadOnlyList<SdpCodecDefinition> audioCodecs) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = audioCodecs,
                Video = new SdpVideoMediaOptions
                {
                    Port = 6002,
                    Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                },
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance);

    // A remote WebRTC offer (BUNDLE + DTLS + ICE + video), built with the negotiator and serialized.
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
