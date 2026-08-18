using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Receive-side simulcast negotiation, session half (#317 slice 2, RFC 8853 §5.3 / RFC 8852): an offer asking
/// <c>a=rid … recv</c> that the peer's answer confirms as <c>send</c> (echoing the RID header extension) makes
/// the built session admit those layers and expose the confirmed set as <see cref="BundledMediaSession.VideoReceiveRids"/>.
/// The mirror image of the send-side <see cref="WebRtcSimulcastOfferTests"/>: there the answer confirms recv,
/// here it confirms send.
/// </summary>
public sealed class WebRtcRecvSimulcastNegotiationTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public async Task The_confirmed_recv_layers_are_exposed_on_the_session()
    {
        await using var session = BuildSession(recvRids: ["hi", "lo"], ConfirmingSendAnswer(["hi", "lo"]));

        Assert.NotNull(session);
        Assert.Equal(["hi", "lo"], session!.VideoReceiveRids.OrderBy(r => r == "hi" ? 0 : 1));
    }

    [Fact]
    public async Task Only_the_layers_the_answer_confirms_are_negotiated()
    {
        // We ask for three; the peer says it will send two. RFC 8853 §5.1: the negotiated set is the
        // intersection — the SFU must not expect a layer the peer never agreed to.
        await using var session = BuildSession(recvRids: ["hi", "mid", "lo"], ConfirmingSendAnswer(["hi", "lo"]));

        Assert.Equal(["hi", "lo"], session!.VideoReceiveRids.OrderBy(r => r == "hi" ? 0 : 1));
    }

    [Fact]
    public async Task Without_the_rid_extension_no_recv_simulcast_is_negotiated()
    {
        // RFC 8852: without the RID header extension the peer cannot tag the layers, so nothing is admitted —
        // even though it listed a=simulcast:send.
        await using var session = BuildSession(recvRids: ["hi", "lo"], ConfirmingSendAnswer(["hi", "lo"], withRidExtension: false));

        Assert.Empty(session!.VideoReceiveRids);
    }

    [Fact]
    public async Task A_recv_offer_a_plain_answer_does_not_confirm_receives_a_single_stream()
    {
        // A peer that simply does not simulcast answers plainly; we must not turn our own recv offer into an
        // allowlist, or that peer's single stream would be dropped.
        await using var session = BuildSession(recvRids: ["hi", "lo"], PlainAnswer());

        Assert.Empty(session!.VideoReceiveRids);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static BundledMediaSession? BuildSession(IReadOnlyList<string> recvRids, SdpSessionDescription remote)
    {
        var local = new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 40080), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264, SimulcastRecvRids = recvRids },
            });

        return WebRtcSessionFactory.TryCreate(
            remote, local,
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                VideoTracks = [new SdpVideoMediaOptions { Port = 6002, Codecs = H264, SimulcastRecvRids = recvRids }],
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);
    }

    private static SdpSessionDescription PlainAnswer() =>
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

    // A peer answer that will SEND simulcast: it echoes the RID header extension (RFC 8852, unless suppressed)
    // and lists the send RIDs (a=rid send + a=simulcast:send). The send-direction mirror of
    // WebRtcSimulcastOfferTests.ConfirmingRemoteAnswer.
    private static SdpSessionDescription ConfirmingSendAnswer(IReadOnlyList<string> sendRids, bool withRidExtension = true)
    {
        var lines = new SdpSessionSerializer().Serialize(PlainAnswer())
            .Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));

        var inject = new List<string>();
        if (withRidExtension)
        {
            var usedIds = lines
                .Where(l => l.StartsWith("a=extmap:", StringComparison.Ordinal))
                .Select(l => l["a=extmap:".Length..].Split(' ')[0])
                .ToHashSet(StringComparer.Ordinal);
            var ridId = Enumerable.Range(1, 14).First(i => !usedIds.Contains(i.ToString()));
            inject.Add($"a=extmap:{ridId} {RtpHeaderExtensionUris.Rid}");
        }

        inject.AddRange(sendRids.Select(r => $"a=rid:{r} send"));
        inject.Add("a=simulcast:send " + string.Join(';', sendRids));
        lines.InsertRange(videoIdx + 1, inject);
        return new SdpSessionParser().Parse(string.Join("\r\n", lines));
    }
}
