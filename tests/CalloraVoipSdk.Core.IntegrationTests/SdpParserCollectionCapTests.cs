using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1-1 (part 2): each per-media-section collection the parser appends to (payload types, fmtp,
/// rtcp-fb, extmap, rid, candidate, crypto) is bounded by a typed cap. A single m= section could otherwise hold
/// as many entries as the whole body's line budget allows; an over-limit section is a controlled parse failure
/// (K4), never an unbounded allocation. These tests drive small caps so the boundary is exercised directly.
/// </summary>
public sealed class SdpParserCollectionCapTests
{
    private const int Cap = 3;

    private static readonly SdpParserLimits SmallLimits = new()
    {
        MaxPayloadTypesPerMedia = Cap,
        MaxFmtpPerMedia = Cap,
        MaxRtcpFeedbackPerMedia = Cap,
        MaxHeaderExtensionsPerMedia = Cap,
        MaxRidsPerMedia = Cap,
        MaxIceCandidatesPerMedia = Cap,
        MaxCryptoPerMedia = Cap,
    };

    private const string Header =
        "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";

    private static string AttributeLine(string attribute, int i) => attribute switch
    {
        "fmtp" => $"a=fmtp:0 minptime={i}\r\n",
        "rtcp-fb" => "a=rtcp-fb:0 nack\r\n",
        "extmap" => $"a=extmap:{i} urn:x\r\n",
        "rid" => $"a=rid:r{i} send\r\n",
        "candidate" => $"a=candidate:{i} 1 udp 2130706431 192.168.0.1 40000 typ host\r\n",
        "crypto" => $"a=crypto:{i} AES_CM_128_HMAC_SHA1_80 inline:abc\r\n",
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null),
    };

    private static string BuildWith(string attribute, int count)
    {
        var body = Header + "m=audio 40000 RTP/AVP 0\r\n";
        for (var i = 0; i < count; i++)
            body += AttributeLine(attribute, i);
        return body;
    }

    [Theory]
    [InlineData("fmtp")]
    [InlineData("rtcp-fb")]
    [InlineData("extmap")]
    [InlineData("rid")]
    [InlineData("candidate")]
    [InlineData("crypto")]
    public void A_media_section_collection_at_the_cap_parses_but_over_the_cap_is_rejected(string attribute)
    {
        var parser = new SdpSessionParser(SmallLimits);

        Assert.True(parser.TryParse(BuildWith(attribute, Cap), out var atCap), $"{attribute} at the cap should parse");
        Assert.NotNull(atCap);

        Assert.False(parser.TryParse(BuildWith(attribute, Cap + 1), out var overCap), $"{attribute} over the cap should be rejected");
        Assert.Null(overCap);
    }

    [Fact]
    public void The_payload_type_list_on_the_m_line_is_capped()
    {
        var parser = new SdpSessionParser(SmallLimits);

        Assert.True(parser.TryParse(Header + "m=audio 40000 RTP/AVP 0 1 2\r\n", out _));
        Assert.False(parser.TryParse(Header + "m=audio 40000 RTP/AVP 0 1 2 3\r\n", out _));
    }

    [Fact]
    public void Rtpmap_declaring_distinct_payload_types_beyond_the_cap_is_rejected()
    {
        var parser = new SdpSessionParser(SmallLimits);

        // The m= line seeds payload type 0; two more distinct rtpmap PTs reach the cap of 3.
        Assert.True(parser.TryParse(
            Header + "m=audio 40000 RTP/AVP 0\r\na=rtpmap:96 OPUS/48000\r\na=rtpmap:97 OPUS/48000\r\n", out _));

        Assert.False(parser.TryParse(
            Header + "m=audio 40000 RTP/AVP 0\r\na=rtpmap:96 OPUS/48000\r\na=rtpmap:97 OPUS/48000\r\na=rtpmap:98 OPUS/48000\r\n", out _));
    }

    [Fact]
    public void A_realistic_offer_stays_within_the_default_caps()
    {
        var parser = new SdpSessionParser();
        var sdp =
            Header +
            "m=audio 40000 RTP/AVP 111 0 8\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n" +
            "a=rtpmap:0 PCMU/8000\r\n" +
            "a=rtpmap:8 PCMA/8000\r\n" +
            "a=fmtp:111 minptime=10\r\n" +
            "a=rtcp-fb:111 nack\r\n" +
            "a=extmap:1 urn:ietf:params:rtp-hdrext:ssrc-audio-level\r\n";

        Assert.True(parser.TryParse(sdp, out var result));
        Assert.NotNull(result);
    }
}
