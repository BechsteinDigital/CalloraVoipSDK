using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP <c>a=msid</c> wire support (RFC 8830, ADR-009 §3 track identity): the parser recovers the
/// MediaStream/track identity of a media section, the serializer emits it, and the value round-trips.
/// The stream id is mandatory; the track-id appdata is optional.
/// </summary>
public sealed class SdpMsidTests
{
    private const string SdpWithMsid =
        "v=0\r\n" +
        "o=- 1 1 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "t=0 0\r\n" +
        "m=audio 5000 UDP/TLS/RTP/SAVPF 111\r\n" +
        "a=mid:audio\r\n" +
        "a=msid:stream-abc track-xyz\r\n" +
        "a=rtpmap:111 opus/48000/2\r\n";

    [Fact]
    public void TryParse_reads_stream_and_track()
    {
        var msid = SdpMsid.TryParse("stream-abc track-xyz");

        Assert.NotNull(msid);
        Assert.Equal("stream-abc", msid!.StreamId);
        Assert.Equal("track-xyz", msid.TrackId);
        Assert.Equal("stream-abc track-xyz", msid.Serialize());
    }

    [Fact]
    public void TryParse_reads_stream_only()
    {
        var msid = SdpMsid.TryParse("stream-only");

        Assert.NotNull(msid);
        Assert.Equal("stream-only", msid!.StreamId);
        Assert.Null(msid.TrackId);
        Assert.Equal("stream-only", msid.Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_is_null_for_empty(string? value)
        => Assert.Null(SdpMsid.TryParse(value!));

    [Fact]
    public void Parser_recovers_the_msid_from_a_media_section()
    {
        var session = new SdpSessionParser().Parse(SdpWithMsid);

        var audio = Assert.Single(session.Media);
        Assert.NotNull(audio.Msid);
        Assert.Equal("stream-abc", audio.Msid!.StreamId);
        Assert.Equal("track-xyz", audio.Msid.TrackId);
    }

    [Fact]
    public void Serialize_then_reparse_round_trips_the_msid()
    {
        var session = new SdpSessionParser().Parse(SdpWithMsid);

        var sdp = new SdpSessionSerializer().Serialize(session);
        Assert.Contains("a=msid:stream-abc track-xyz", sdp, StringComparison.Ordinal);

        var reparsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal(session.Media.Single().Msid, reparsed.Media.Single().Msid);
    }

    [Fact]
    public void A_bundle_offer_emits_a_msid_on_audio_and_video_with_a_shared_stream()
    {
        const string streamId = "stream-1";
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000),
            [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
            SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                AudioMsid = new SdpMsid { StreamId = streamId, TrackId = "audio-track" },
                Video = new SdpVideoMediaOptions
                {
                    Port = 5002,
                    Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                    Msid = new SdpMsid { StreamId = streamId, TrackId = "video-track" },
                },
            });

        var audio = offer.Media.Single(m => m.MediaType == "audio");
        var video = offer.Media.Single(m => m.MediaType == "video");

        Assert.Equal(streamId, audio.Msid!.StreamId);
        Assert.Equal("audio-track", audio.Msid.TrackId);
        Assert.Equal(streamId, video.Msid!.StreamId);        // one MediaStream across both tracks
        Assert.Equal("video-track", video.Msid.TrackId);

        var sdp = new SdpSessionSerializer().Serialize(offer);
        Assert.Contains("a=msid:stream-1 audio-track", sdp, StringComparison.Ordinal);
        Assert.Contains("a=msid:stream-1 video-track", sdp, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offer_without_msid_options_emits_no_msid()
    {
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000),
            [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
            SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true });

        Assert.Null(offer.Media.Single(m => m.MediaType == "audio").Msid);
        Assert.DoesNotContain("a=msid", new SdpSessionSerializer().Serialize(offer), StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_without_msid_has_none_and_emits_none()
    {
        const string sdp =
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n" +
            "m=audio 5000 UDP/TLS/RTP/SAVPF 111\r\n" +
            "a=mid:audio\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n";

        var session = new SdpSessionParser().Parse(sdp);
        Assert.Null(session.Media.Single().Msid);
        Assert.DoesNotContain("a=msid", new SdpSessionSerializer().Serialize(session), StringComparison.Ordinal);
    }
}
