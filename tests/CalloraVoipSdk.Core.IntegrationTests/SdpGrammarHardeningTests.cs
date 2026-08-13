using System.Linq;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-10, P2-11 and P2-12: three places where the grammar was read approximately. A substring
/// match that says yes to the wrong value, a legal field shape that failed the whole parse, and a
/// mandatory line nobody checked for. Each fails quietly — the description looks parsed, and what it
/// says is not what the peer wrote.
/// </summary>
public sealed class SdpGrammarHardeningTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";

    private static SdpSessionDescription? Parse(string sdp) =>
        new SdpSessionParser().TryParse(Header + sdp, out var parsed) ? parsed : null;

    // ── P2-10: packetization-mode as an exact key/value (RFC 6184 §8.1) ───────

    [Theory]
    [InlineData("packetization-mode=1")]
    [InlineData("profile-level-id=42e01f;packetization-mode=1")]
    [InlineData("packetization-mode=1;profile-level-id=42e01f")]
    [InlineData("packetization-mode = 1")]                        // whitespace around the separator
    [InlineData("PACKETIZATION-MODE=1")]                          // the key is case-insensitive
    public void Mode_1_is_recognised_when_it_is_actually_declared(string parameters)
    {
        var fmtp = new[] { new SdpFmtpAttribute { PayloadType = 96, Parameters = parameters } };

        Assert.True(VideoCodecCatalog.HasPacketizationMode1(fmtp, 96));
    }

    [Theory]
    [InlineData("packetization-mode=10")]      // the review's probe: mode 10 read as mode 1
    [InlineData("packetization-mode=0")]
    [InlineData("packetization-mode=2")]
    [InlineData("packetization-mode=12")]
    [InlineData("profile-level-id=42packetization-mode=1")]   // the text appears inside another value
    [InlineData("x-packetization-mode=1")]                    // a different parameter ending in the key
    [InlineData("packetization-mode")]                        // no value at all
    [InlineData("")]
    public void A_value_that_is_not_mode_1_is_not_read_as_mode_1(string parameters)
    {
        var fmtp = new[] { new SdpFmtpAttribute { PayloadType = 96, Parameters = parameters } };

        // Mode 0 means the peer cannot receive the FU-A fragments this stack emits. Answering "yes" to
        // "packetization-mode=10" produced video the far end silently discarded.
        Assert.False(VideoCodecCatalog.HasPacketizationMode1(fmtp, 96));
    }

    [Fact]
    public void Mode_1_on_another_payload_type_does_not_count_for_this_one()
    {
        var fmtp = new[] { new SdpFmtpAttribute { PayloadType = 97, Parameters = "packetization-mode=1" } };

        Assert.False(VideoCodecCatalog.HasPacketizationMode1(fmtp, 96));
    }

    // ── P2-11a: <port>/<number of ports> (RFC 8866 §5.14) ────────────────────

    [Fact]
    public void A_port_range_no_longer_fails_the_whole_description()
    {
        // The review's probe: "m=video 40000/2 …" is legal SDP for a hierarchically encoded stream, and
        // rejecting the field rejected every other m-line with it — the call got no answer at all.
        var parsed = Parse("m=video 40000/2 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n");

        Assert.NotNull(parsed);
        Assert.Equal(40000, parsed!.Media[0].Port);
        Assert.Equal(2, parsed.Media[0].PortCount);
        Assert.Contains(parsed.Media[0].Codecs, c => c.PayloadType == 96);
    }

    [Fact]
    public void A_media_line_without_the_suffix_reports_a_single_port()
    {
        var parsed = Parse("m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.Equal(1, parsed?.Media[0].PortCount);
    }

    [Theory]
    [InlineData("40000/0")]        // a section occupying no ports is not a section
    [InlineData("40000/-1")]
    [InlineData("40000/abc")]
    [InlineData("65535/4")]        // the range would run past the 16-bit port space
    public void A_port_range_that_cannot_exist_still_fails_the_parse(string portField)
    {
        Assert.Null(Parse($"m=audio {portField} RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"));
    }

    // ── P2-11b: opaque formats under a non-RTP profile (RFC 8866 §5.14) ──────

    [Fact]
    public void A_non_rtp_profile_keeps_its_opaque_format()
    {
        // The review's probe: the fmt of a data channel names a protocol, not a payload type. Parsing it
        // as an integer dropped it, leaving a section whose format nobody could read back.
        var parsed = Parse("m=application 5000 UDP/DTLS/SCTP webrtc-datachannel\r\n");

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "webrtc-datachannel" }, parsed!.Media[0].Formats);
        Assert.Empty(parsed.Media[0].Codecs);
    }

    [Fact]
    public void An_opaque_format_survives_a_serialise_round_trip()
    {
        // The consequence that matters: rebuilding the line from Codecs alone emitted an m-line with an
        // empty fmt field, which is not valid SDP.
        var parsed = Parse("m=application 5000 UDP/DTLS/SCTP webrtc-datachannel\r\n");
        var text = new SdpSessionSerializer().Serialize(parsed!);

        Assert.Contains("m=application 5000 UDP/DTLS/SCTP webrtc-datachannel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_rtp_profile_still_exposes_its_formats_as_payload_types()
    {
        var parsed = Parse("m=audio 40000 RTP/AVP 0 8\r\na=rtpmap:0 PCMU/8000\r\na=rtpmap:8 PCMA/8000\r\n");

        Assert.Equal(new[] { "0", "8" }, parsed!.Media[0].Formats);
        Assert.Equal(new[] { 0, 8 }, parsed.Media[0].Codecs.Select(c => c.PayloadType));
    }

    // ── P2-12: o= is mandatory (RFC 8866 §5.2) ───────────────────────────────

    [Fact]
    public void A_description_without_an_origin_line_is_rejected()
    {
        // o= carries the session id and version that tell a re-offer apart from a new session
        // (RFC 3264 §8). Without it OriginAddress silently stayed at whatever it had defaulted to.
        const string noOrigin = "v=0\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\nm=audio 40000 RTP/AVP 0\r\n";

        Assert.False(new SdpSessionParser().TryParse(noOrigin, out _));
    }

    [Theory]
    [InlineData("o=-")]
    [InlineData("o=- 0 0")]
    [InlineData("o=- 0 0 IN IP4")]                    // truncated before the address
    [InlineData("o=- 0 0 IN IP4 127.0.0.1 extra")]    // one field too many
    public void An_origin_line_without_its_six_fields_is_rejected(string originLine)
    {
        var sdp = $"v=0\r\n{originLine}\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\nm=audio 40000 RTP/AVP 0\r\n";

        Assert.False(new SdpSessionParser().TryParse(sdp, out _));
    }

    [Fact]
    public void A_complete_origin_line_parses_and_yields_its_address()
    {
        var parsed = Parse("m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.Equal("127.0.0.1", parsed?.OriginAddress);
    }
}
