using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1-b follow-up: on the offerer path the session factory must send the codec the answer
/// accepted, not merely its first offered format. The effective send codec is the first local codec (in
/// local preference) that the paired remote section also lists by payload type (RFC 3264 §6.1).
/// </summary>
public sealed class WebRtcEffectiveCodecTests
{
    private static SdpCodecDefinition Codec(int pt, string name) =>
        new() { PayloadType = pt, Name = name, ClockRate = name == "opus" ? 48000 : 8000 };

    private static SdpMediaDescription Media(string type, params int[] pts) => new()
    {
        MediaType = type,
        Port = 6002,
        Profile = "UDP/TLS/RTP/SAVPF",
        Direction = SdpMediaDirection.SendRecv,
        Codecs = pts.Select(pt => Codec(pt, $"C{pt}")).ToArray(),
    };

    [Fact]
    public void Picks_the_first_local_codec_the_remote_also_lists()
    {
        // Local prefers opus (111) then PCMU (0); the remote accepted only PCMU → send PCMU.
        var local = new[] { Codec(111, "opus"), Codec(0, "PCMU") };

        var picked = WebRtcSessionFactory.FirstSendableCodec(local, Media("audio", 0), _ => true);

        Assert.NotNull(picked);
        Assert.Equal(0, picked!.PayloadType);
    }

    [Fact]
    public void Keeps_local_preference_when_the_remote_lists_both()
    {
        var local = new[] { Codec(111, "opus"), Codec(0, "PCMU") };

        var picked = WebRtcSessionFactory.FirstSendableCodec(local, Media("audio", 111, 0), _ => true);

        Assert.Equal(111, picked!.PayloadType); // first local that is also remote
    }

    [Fact]
    public void Returns_null_when_there_is_no_common_codec()
    {
        var local = new[] { Codec(111, "opus") };

        Assert.Null(WebRtcSessionFactory.FirstSendableCodec(local, Media("audio", 0), _ => true));
    }

    [Fact]
    public void Applies_the_keep_predicate_alongside_the_intersection()
    {
        // telephone-event (101) is common but excluded by the predicate; PCMU (0) is the codec.
        var local = new[] { Codec(0, "PCMU"), Codec(101, "telephone-event") };

        var picked = WebRtcSessionFactory.FirstSendableCodec(
            local, Media("audio", 0, 101), c => !c.Name.Equals("telephone-event", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, picked!.PayloadType);
    }

    [Fact]
    public void Video_track_sends_the_codec_the_answer_accepted_not_the_first_offered()
    {
        // Offerer offers VP8 (96, first) and H264 (97); the answer accepted only H264.
        var offer = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 96 97\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 H264/90000\r\n" +
            "a=mid:1\r\na=sendrecv\r\n");
        var answer = new SdpSessionParser().Parse(
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 6002 UDP/TLS/RTP/SAVPF 97\r\na=rtpmap:97 H264/90000\r\na=mid:1\r\na=recvonly\r\n");
        var localVideo = offer.Media.First(m => m.MediaType == "video");

        var config = WebRtcSessionFactory.TryBuildVideoTrack(
            localVideo, answer, new HashSet<uint>(), NullLoggerFactory.Instance);

        Assert.NotNull(config);
        Assert.Equal(97, config!.PayloadType); // H264 (accepted), not 96 (VP8, first offered)
    }
}
