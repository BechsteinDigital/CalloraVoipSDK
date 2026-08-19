using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Answerer-side simulcast negotiation (#369, RFC 8853 §5.3 / RFC 8852): the mirror of the offerer path
/// (<see cref="WebRtcSimulcastOfferTests"/> / <see cref="WebRtcRecvSimulcastNegotiationTests"/>). An offer
/// that declares <c>a=simulcast:send</c> is confirmed in the answer with <c>a=simulcast:recv</c> (case A —
/// the common SFU topology, this side receives the layers); an offer that asks <c>a=simulcast:recv</c> is
/// answered with <c>a=simulcast:send</c> for the layers the local options are configured to produce (case B).
/// Only the intersection is confirmed, only when the offer carried the RID header extension, and only for two
/// or more distinct layers; an offer without simulcast is answered byte-identically to before.
/// </summary>
public sealed class WebRtcAnswererSimulcastNegotiationTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    // ── case A: offered send → answered recv ───────────────────────────────────

    [Fact]
    public void Answering_a_send_simulcast_offer_confirms_recv()
    {
        var video = AnswerVideo(OffererSendSimulcast(["hi", "lo"]));

        // a=simulcast:recv + one a=rid recv per layer, restricted to the answered codec.
        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "lo"], video.Simulcast!.Recv);
        Assert.Empty(video.Simulcast.Send);
        Assert.Equal(["hi", "lo"], video.Rids.Select(r => r.Id));
        Assert.All(video.Rids, r => Assert.Equal("recv", r.Direction));
        Assert.All(video.Rids, r => Assert.Equal("pt=96", r.Restrictions));

        // The RID header extension (RFC 8852) is echoed under the offered id — without it the confirmation is
        // worthless (the peer cannot label the layers it sends).
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public void Answering_a_send_simulcast_offer_confirms_every_offered_layer()
    {
        // We receive whatever the peer will send — three layers here, all admitted (we never ask for fewer).
        var video = AnswerVideo(OffererSendSimulcast(["hi", "mid", "lo"]));

        Assert.Equal(["hi", "mid", "lo"], video.Simulcast!.Recv);
    }

    // ── case B: offered recv → answered send ───────────────────────────────────

    [Fact]
    public void Answering_a_recv_simulcast_offer_confirms_send_for_the_configured_layers()
    {
        var video = AnswerVideo(OffererRecvSimulcast(["hi", "lo"]), localSendRids: ["hi", "lo"]);

        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "lo"], video.Simulcast!.Send);
        Assert.Empty(video.Simulcast.Recv);
        Assert.Equal(["hi", "lo"], video.Rids.Select(r => r.Id));
        Assert.All(video.Rids, r => Assert.Equal("send", r.Direction));
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public void Only_the_layers_both_sides_named_are_confirmed_on_send()
    {
        // The peer asks for three; we are configured to produce two. RFC 8853 §5.1: only the intersection,
        // in the offer's order.
        var video = AnswerVideo(OffererRecvSimulcast(["hi", "mid", "lo"]), localSendRids: ["lo", "hi"]);

        Assert.Equal(["hi", "lo"], video.Simulcast!.Send);
    }

    [Fact]
    public void A_recv_offer_we_are_not_configured_to_simulcast_is_answered_as_a_single_stream()
    {
        // The peer asks us to simulcast, but the local options offer no send layers — we cannot produce them,
        // so we answer a plain single stream rather than promise layers we will never send.
        var video = AnswerVideo(OffererRecvSimulcast(["hi", "lo"]), localSendRids: []);

        Assert.Null(video.Simulcast);
        Assert.Empty(video.Rids);
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    // ── guards ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_simulcast_offer_without_the_rid_extension_is_not_confirmed()
    {
        // RFC 8852: without the RID header extension the layers cannot be labelled per packet, so a
        // confirmation would be worthless — the answer must decline simulcast (#369 criterion 2).
        var video = AnswerVideo(WithoutRidExtension(OffererSendSimulcast(["hi", "lo"])));

        Assert.Null(video.Simulcast);
        Assert.Empty(video.Rids);
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public void A_single_layer_simulcast_offer_is_not_confirmed()
    {
        // One a=rid is not simulcast (Chrome strips a lone rid); a crafted single-layer offer must not be
        // answered as simulcast (#369, "a single layer is not simulcast").
        var video = AnswerVideo(SingleLayerSendOffer("hi"));

        Assert.Null(video.Simulcast);
        Assert.Empty(video.Rids);
    }

    [Fact]
    public void An_offer_without_simulcast_is_answered_with_no_rid_or_simulcast()
    {
        // No-regress (#369 criterion 7): a plain video offer is answered exactly as before — no a=rid, no
        // a=simulcast, and no RID header extension pulled into the answer.
        var video = AnswerVideo(PlainOffer());

        Assert.Null(video.Simulcast);
        Assert.Empty(video.Rids);
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    // ── the offerer origin also drops a lone layer (#369 "nachgelagert") ────────

    [Fact]
    public void A_single_configured_send_layer_is_not_offered_as_simulcast()
    {
        // The <2-distinct rule lives where the SDP is built: a single configured layer yields a plain offer,
        // not an a=simulcast that looks like the feature and does nothing.
        var video = OffererSendSimulcast(["hi"]).Media.Single(m => m.MediaType == "video");

        Assert.Null(video.Simulcast);
        Assert.Empty(video.Rids);
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private static SdpMediaDescription AnswerVideo(
        SdpSessionDescription offer, IReadOnlyList<string>? localSendRids = null)
    {
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer,
            new IPEndPoint(IPAddress.Loopback, 40080),
            Pcmu,
            SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "active" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                Video = new SdpVideoMediaOptions
                {
                    Port = 6002,
                    Codecs = H264,
                    SimulcastSendRids = localSendRids ?? [],
                },
            });

        Assert.True(result.Success);
        return result.Answer!.Media.Single(m => m.MediaType == "video");
    }

    private static SdpSessionDescription OffererSendSimulcast(IReadOnlyList<string> rids) =>
        Offer(new SdpVideoMediaOptions { Port = 5002, Codecs = H264, SimulcastSendRids = rids });

    private static SdpSessionDescription OffererRecvSimulcast(IReadOnlyList<string> rids) =>
        Offer(new SdpVideoMediaOptions { Port = 5002, Codecs = H264, SimulcastRecvRids = rids });

    private static SdpSessionDescription PlainOffer() =>
        Offer(new SdpVideoMediaOptions { Port = 5002, Codecs = H264 });

    private static SdpSessionDescription Offer(SdpVideoMediaOptions video) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
                Video = video,
            });

    // Re-parses an offer with the RID header extension stripped from its video section, leaving the
    // a=simulcast/a=rid lines intact — the "declares simulcast but cannot label it" case.
    private static SdpSessionDescription WithoutRidExtension(SdpSessionDescription offer)
    {
        var lines = new SdpSessionSerializer().Serialize(offer)
            .Replace("\r\n", "\n").Split('\n')
            .Where(l => !(l.StartsWith("a=extmap:", StringComparison.Ordinal) && l.Contains(RtpHeaderExtensionUris.Rid, StringComparison.Ordinal)))
            .ToList();
        return new SdpSessionParser().Parse(string.Join("\r\n", lines));
    }

    // A crafted offer whose video section carries a single a=rid send + a=simulcast:send + the RID extension —
    // the shape the builder would never emit (it drops a lone layer), used to prove the answer drops it too.
    private static SdpSessionDescription SingleLayerSendOffer(string rid)
    {
        var lines = new SdpSessionSerializer().Serialize(PlainOffer())
            .Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));

        var usedIds = lines
            .Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))
            .Select(l => l["a=extmap:".Length..].Split(' ')[0])
            .ToHashSet(StringComparer.Ordinal);
        var ridId = Enumerable.Range(1, 14).First(i => !usedIds.Contains(i.ToString()));

        lines.InsertRange(videoIdx + 1, new[]
        {
            $"a=extmap:{ridId} {RtpHeaderExtensionUris.Rid}",
            $"a=rid:{rid} send",
            $"a=simulcast:send {rid}",
        });
        return new SdpSessionParser().Parse(string.Join("\r\n", lines));
    }
}
