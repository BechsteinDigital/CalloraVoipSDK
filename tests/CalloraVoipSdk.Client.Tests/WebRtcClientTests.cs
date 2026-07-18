using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The public WebRTC facade (ADR-012, Level 1): <see cref="WebRtcClient"/> builds a signalling-neutral
/// <see cref="IPeerConnection"/> that offers/answers a WebRTC session (BUNDLE, DTLS-SRTP, msid) without
/// exposing any internal type. Exercised through the public surface only.
/// </summary>
public sealed class WebRtcClientTests
{
    [Fact]
    public async Task CreatePeer_produces_a_webrtc_bundle_dtls_offer_with_msid()
    {
        var rtc = new WebRtcClient(new WebRtcConfiguration { EnableVideo = true });
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        Assert.Contains("a=group:BUNDLE", offer, StringComparison.Ordinal);
        Assert.Contains("UDP/TLS/RTP/SAVPF", offer, StringComparison.Ordinal);   // WebRTC DTLS-SRTP profile
        Assert.Contains("a=fingerprint:", offer, StringComparison.Ordinal);
        Assert.Contains("a=msid:", offer, StringComparison.Ordinal);              // track identity (ADR-012 §4)
        Assert.Equal(PeerConnectionState.New, peer.State);
    }

    [Fact]
    public async Task Two_peers_negotiate_an_offer_and_answer()
    {
        // Early-bind binds the media socket at CreateOffer, so even an ephemeral (port 0) config advertises a
        // live m-line — no fixed port needed (a fixed port collides on CI). The answerer binds its own
        // ephemeral port when it applies the offer.
        var offererClient = new WebRtcClient(new WebRtcConfiguration { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        var answererClient = new WebRtcClient();
        await using var offerer = offererClient.CreatePeer();
        await using var answerer = answererClient.CreatePeer();

        var offer = offerer.CreateOffer();
        var answer = await answerer.SetRemoteDescriptionAsync(offer);

        Assert.Contains("a=fingerprint:", answer, StringComparison.Ordinal);    // DTLS-SRTP-keyed answer
        Assert.Equal(PeerConnectionState.Connecting, answerer.State);
        Assert.NotNull(answerer.LocalMediaEndPoint);                            // the media transport bound
    }

    [Fact]
    public void An_unknown_codec_is_rejected()
    {
        var rtc = new WebRtcClient(new WebRtcConfiguration { AudioCodecs = ["nope"] });
        Assert.Throws<ArgumentException>(() => rtc.CreatePeer());
    }
}
