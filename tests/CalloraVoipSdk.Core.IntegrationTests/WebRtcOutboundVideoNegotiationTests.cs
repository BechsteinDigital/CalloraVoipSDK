using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound video is negotiated, not built blindly from the local description (RFC 8829/3264): the session
/// factory builds a send-side video track only when this peer sends AND the remote will receive it. A remote
/// answer that rejected video (inactive / send-only / absent) yields no outbound video track — the outbound
/// mirror of the inbound port/direction gate (external-review finding #4). A recv-only remote (asymmetric)
/// still receives our video. Audio always keys the session (the bundle transport anchor).
/// </summary>
public sealed class WebRtcOutboundVideoNegotiationTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Theory]
    [InlineData("sendrecv")] // remote sends and receives -> it receives our video
    [InlineData("recvonly")] // remote only receives (asymmetric) -> it still receives our video
    public async Task Outbound_video_is_built_when_the_remote_will_receive_it(string remoteVideoDirection)
    {
        var session = BuildSession(Offer(), RemoteAnswerWithVideoDirection(remoteVideoDirection));

        Assert.NotNull(session);
        await using var _ = session;
        Assert.True(session!.HasVideo);
    }

    [Theory]
    [InlineData("inactive")] // remote will not receive
    [InlineData("sendonly")] // remote sends only; it will not receive our video
    public async Task No_outbound_video_track_when_the_remote_will_not_receive(string remoteVideoDirection)
    {
        var session = BuildSession(Offer(), RemoteAnswerWithVideoDirection(remoteVideoDirection));

        Assert.NotNull(session);          // audio still keys the session (the bundle transport anchor)
        await using var _ = session;
        Assert.False(session!.HasVideo);  // but no video is streamed to a remote that will not receive it
    }

    [Fact]
    public async Task No_outbound_video_track_when_the_remote_omits_the_video_section()
    {
        var session = BuildSession(Offer(), AudioOnlyRemoteAnswer());

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.HasVideo);
    }

    [Fact]
    public async Task No_outbound_video_track_when_the_remote_rejected_video_with_a_zero_port()
    {
        // RFC 3264 §6: a rejected media section carries port 0. The disabled m-line must yield no outbound
        // video track even though its direction would otherwise permit it.
        var session = BuildSession(Offer(), RemoteAnswerWithRejectedVideo());

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.HasVideo);
    }

    // Builds the bundle session from a negotiated exchange (this peer offers audio + video send-recv).
    private static BundledMediaSession? BuildSession(SdpSessionDescription local, SdpSessionDescription remote) =>
        WebRtcSessionFactory.TryCreate(
            remote, local,
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264 },
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

    // Our local offer: audio + video, send-recv (BUNDLE, so MID/sdes:mid are emitted).
    private static SdpSessionDescription Offer() =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 40090), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                Video = new SdpVideoMediaOptions { Port = 6002, Codecs = H264 },
            });

    // The peer's answer that keys the session, with its video section forced to the given direction. Built by
    // rewriting the video section's direction attribute (round-tripped through the serializer/parser).
    private static SdpSessionDescription RemoteAnswerWithVideoDirection(string direction)
    {
        var lines = new SdpSessionSerializer().Serialize(PlainRemoteAnswer())
            .Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));
        var sectionEnd = lines.FindIndex(videoIdx + 1, l => l.StartsWith("m=", StringComparison.Ordinal));
        if (sectionEnd < 0)
            sectionEnd = lines.Count;

        // Drop any existing direction attribute in the video section, then set the requested one.
        for (var i = sectionEnd - 1; i > videoIdx; i--)
            if (IsDirectionAttribute(lines[i]))
                lines.RemoveAt(i);
        lines.Insert(videoIdx + 1, "a=" + direction);

        return new SdpSessionParser().Parse(string.Join("\r\n", lines));

        static bool IsDirectionAttribute(string line) =>
            line is "a=sendrecv" or "a=sendonly" or "a=recvonly" or "a=inactive";
    }

    // The peer's plain answer with an audio + video section (both send-recv).
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

    // The peer's answer that rejected video by zeroing the m=video port (RFC 3264 §6 rejected media).
    private static SdpSessionDescription RemoteAnswerWithRejectedVideo()
    {
        var lines = new SdpSessionSerializer().Serialize(PlainRemoteAnswer())
            .Replace("\r\n", "\n").Split('\n').ToList();
        var videoIdx = lines.FindIndex(l => l.StartsWith("m=video ", StringComparison.Ordinal));
        var parts = lines[videoIdx].Split(' ');
        parts[1] = "0"; // zero the port -> rejected / disabled m-line
        lines[videoIdx] = string.Join(' ', parts);

        return new SdpSessionParser().Parse(string.Join("\r\n", lines));
    }

    // The peer's answer with audio only (no video section at all).
    private static SdpSessionDescription AudioOnlyRemoteAnswer() =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "active" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
            });
}
