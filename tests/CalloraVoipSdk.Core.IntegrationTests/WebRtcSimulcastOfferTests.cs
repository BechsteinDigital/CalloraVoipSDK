using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Send-side simulcast negotiation (RFC 8853): a simulcast-configured offer advertises one <c>a=rid …
/// send</c> per layer restricted to the video codec, an <c>a=simulcast:send</c> declaration, and the RID
/// header extension (RFC 8852); the session factory then builds one keyed encoding per layer, each on its
/// own SSRC. A non-simulcast offer is unchanged.
/// </summary>
public sealed class WebRtcSimulcastOfferTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public void A_simulcast_offer_advertises_rids_simulcast_and_the_rid_extension()
    {
        var offer = SimulcastOffer(["hi", "mid", "lo"]);
        var video = offer.Media.Single(m => m.MediaType == "video");

        // One a=rid per layer, direction send, restricted to the primary codec's payload type.
        Assert.Equal(["hi", "mid", "lo"], video.Rids.Select(r => r.Id));
        Assert.All(video.Rids, r => Assert.Equal("send", r.Direction));
        Assert.All(video.Rids, r => Assert.Equal("pt=96", r.Restrictions));

        // a=simulcast:send listing the layers in order.
        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "mid", "lo"], video.Simulcast!.Send);

        // The RID header extension is negotiated; MID keeps the first id, RID the next.
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
        Assert.Equal(1, video.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid).Id);
        Assert.Equal(2, video.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Rid).Id);
    }

    [Fact]
    public void A_non_simulcast_offer_emits_no_rid_or_simulcast()
    {
        var offer = SimulcastOffer([]);
        var video = offer.Media.Single(m => m.MediaType == "video");

        Assert.Empty(video.Rids);
        Assert.Null(video.Simulcast);
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Rid);
    }

    [Fact]
    public async Task The_session_factory_builds_one_keyed_encoding_per_confirmed_rid_on_distinct_ssrcs()
    {
        var local = SimulcastOffer(["hi", "mid", "lo"]);
        var remote = ConfirmingRemoteAnswer(["hi", "mid", "lo"]); // the answer confirms all recv RIDs

        var session = BuildSession(local, remote, ["hi", "mid", "lo"]);

        Assert.NotNull(session);
        await using var _ = session;
        Assert.True(session!.VideoIsSimulcast);
        Assert.Equal(["hi", "mid", "lo"], session.VideoSendRids.OrderBy(r => r switch { "hi" => 0, "mid" => 1, _ => 2 }));
    }

    [Fact]
    public async Task Simulcast_falls_back_to_a_single_stream_when_the_answer_does_not_confirm()
    {
        // RFC 8853: the offer advertises simulcast, but the plain answer confirms no recv RIDs / RID
        // extension — the session must not stamp RIDs the peer cannot demux, so it sends a single stream.
        var local = SimulcastOffer(["hi", "mid", "lo"]);
        var remote = PlainRemoteAnswer();

        var session = BuildSession(local, remote, ["hi", "mid", "lo"]);

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.VideoIsSimulcast);
        Assert.Empty(session.VideoSendRids);
    }

    [Fact]
    public async Task Only_the_rids_the_answer_confirms_are_activated()
    {
        // The answer accepts only "hi" and "lo" (drops "mid") — only those two layers are keyed.
        var local = SimulcastOffer(["hi", "mid", "lo"]);
        var remote = ConfirmingRemoteAnswer(["hi", "lo"]);

        var session = BuildSession(local, remote, ["hi", "mid", "lo"]);

        Assert.NotNull(session);
        await using var _ = session;
        Assert.True(session!.VideoIsSimulcast);
        Assert.Equal(["hi", "lo"], session.VideoSendRids.OrderBy(r => r switch { "hi" => 0, _ => 1 }));
    }

    [Fact]
    public async Task The_session_factory_builds_a_single_stream_when_no_rid_is_offered()
    {
        var local = SimulcastOffer([]);
        var remote = PlainRemoteAnswer();

        var options = new WebRtcPeerOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AudioCodecs = Pcmu,
            Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264 },
            Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
            Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
        };

        var session = WebRtcSessionFactory.TryCreate(
            remote, local, options,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.VideoIsSimulcast);
        Assert.Empty(session.VideoSendRids);
    }

    // Builds the bundle session from a negotiated exchange for the given locally-offered simulcast layers.
    private static BundledMediaSession? BuildSession(
        SdpSessionDescription local, SdpSessionDescription remote, IReadOnlyList<string> simulcastSendRids) =>
        WebRtcSessionFactory.TryCreate(
            remote, local,
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264, SimulcastSendRids = simulcastSendRids },
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

    // A peer answer that ACCEPTS simulcast: it echoes the RID header extension (RFC 8852) and lists the recv
    // RIDs (RFC 8853, a=rid recv + a=simulcast:recv). Built by injecting those lines into a plain answer's
    // video section (round-tripped through the serializer/parser, which cover these attributes).
    private static SdpSessionDescription ConfirmingRemoteAnswer(IReadOnlyList<string> recvRids)
    {
        var lines = new SdpSessionSerializer().Serialize(PlainRemoteAnswer())
            .Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));

        // Pick a RID extmap id (1..14) not already used, so the injected extension never collides.
        var usedIds = lines
            .Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))
            .Select(l => l["a=extmap:".Length..].Split(' ')[0])
            .ToHashSet(StringComparer.Ordinal);
        var ridId = Enumerable.Range(1, 14).First(i => !usedIds.Contains(i.ToString()));

        var inject = new List<string> { $"a=extmap:{ridId} {RtpHeaderExtensionUris.Rid}" };
        inject.AddRange(recvRids.Select(r => $"a=rid:{r} recv"));
        inject.Add("a=simulcast:recv " + string.Join(';', recvRids));
        lines.InsertRange(videoIdx + 1, inject);
        return new SdpSessionParser().Parse(string.Join("\r\n", lines));
    }

    // Our local offer with the given simulcast layers (empty = single stream).
    private static SdpSessionDescription SimulcastOffer(IReadOnlyList<string> rids) =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 40080), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264, SimulcastSendRids = rids },
            });

    // The peer's (plain) description that keys the session — no simulcast on the remote side.
    private static SdpSessionDescription PlainRemoteAnswer() =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "active" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
                Video = new SdpVideoMediaOptions { Port = 5002, Codecs = H264 },
            });
}
