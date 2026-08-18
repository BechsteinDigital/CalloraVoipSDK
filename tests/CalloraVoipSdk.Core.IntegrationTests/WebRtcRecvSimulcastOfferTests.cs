using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Receive-side simulcast negotiation (RFC 8853 §5.3, #317): an offerer that wants the peer to simulcast
/// declares <c>a=rid … recv</c> + <c>a=simulcast:recv</c> and carries the RID header extension (RFC 8852), so
/// the peer it asked can tag each layer it sends back. The send-only and no-simulcast offers are unchanged.
/// </summary>
/// <remarks>
/// This closes the direction the receive pipeline was already built for but could not be asked for: a
/// conference host is the offerer, and an answerer may only simulcast what the offer marked recv — without
/// this declaration the peer on the narrow uplink sends a single stream and forces everyone to its quality.
/// </remarks>
public sealed class WebRtcRecvSimulcastOfferTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public void A_recv_only_offer_advertises_recv_rids_simulcast_recv_and_the_rid_extension()
    {
        var video = Offer(send: [], recv: ["hi", "mid", "lo"]).Media.Single(m => m.MediaType == "video");

        Assert.Equal(["hi", "mid", "lo"], video.Rids.Select(r => r.Id));
        Assert.All(video.Rids, r => Assert.Equal("recv", r.Direction));
        Assert.All(video.Rids, r => Assert.Equal("pt=96", r.Restrictions));

        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "mid", "lo"], video.Simulcast!.Recv);
        Assert.Empty(video.Simulcast.Send);

        // The RID extension must be offered on a recv-only m-line too — otherwise the peer cannot tag the
        // layers it sends, which is the whole point of asking.
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public void An_offer_can_ask_recv_and_send_at_once()
    {
        var video = Offer(send: ["a"], recv: ["hi", "lo"]).Media.Single(m => m.MediaType == "video");

        Assert.Equal(["a"], video.Rids.Where(r => r.Direction == "send").Select(r => r.Id));
        Assert.Equal(["hi", "lo"], video.Rids.Where(r => r.Direction == "recv").Select(r => r.Id));
        Assert.Equal(["a"], video.Simulcast!.Send);
        Assert.Equal(["hi", "lo"], video.Simulcast.Recv);
    }

    [Fact]
    public void A_send_only_offer_is_unaffected_by_the_recv_plumbing()
    {
        // Criterion #4: adding the recv path must not change a send-only offer. Serialized, it carries
        // a=simulcast:send and no recv token anywhere.
        var sdp = new SdpSessionSerializer().Serialize(Offer(send: ["hi", "lo"], recv: []));

        Assert.Contains("a=simulcast:send hi;lo", sdp, StringComparison.Ordinal);
        // Precise recv tokens — "recv" alone also appears in a=sendrecv (the media direction).
        Assert.DoesNotContain("a=simulcast:recv", sdp, StringComparison.Ordinal);
        Assert.DoesNotContain(" recv ", sdp, StringComparison.Ordinal);   // a=rid:<id> recv <restrictions>
        Assert.DoesNotContain(" recv\r", sdp, StringComparison.Ordinal);  // a=rid:<id> recv (no restrictions)
    }

    [Fact]
    public void A_plain_offer_carries_no_rid_or_simulcast_of_either_direction()
    {
        // Byte-identity floor: with neither send nor recv rids, nothing simulcast-related is emitted and the
        // RID extension stays off.
        var sdp = new SdpSessionSerializer().Serialize(Offer(send: [], recv: []));

        Assert.DoesNotContain("a=rid:", sdp, StringComparison.Ordinal);
        Assert.DoesNotContain("a=simulcast:", sdp, StringComparison.Ordinal);
        var video = Offer(send: [], recv: []).Media.Single(m => m.MediaType == "video");
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    private static SdpSessionDescription Offer(IReadOnlyList<string> send, IReadOnlyList<string> recv) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 40080), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                Video = new SdpVideoMediaOptions
                {
                    Port = 6002, Codecs = H264, SimulcastSendRids = send, SimulcastRecvRids = recv,
                },
            });
}
