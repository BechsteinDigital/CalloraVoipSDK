using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1 (part 1): the parser must bound an untrusted remote SDP before it splits and allocates,
/// and expose a non-throwing contract so a malformed body never crashes the signalling path (K4).
/// A duplicate payload type previously threw ArgumentException out of ToDictionary, escaping the
/// FormatException/null Try* contract; the caps and the TryParse contract are pinned here.
/// </summary>
public sealed class SdpParserHardeningTests
{
    private const string ValidSdp =
        "v=0\r\n" +
        "o=- 0 0 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "m=audio 40000 RTP/AVP 0\r\n" +
        "a=rtpmap:0 PCMU/8000\r\n";

    // A duplicate payload type on the m-line: "RTP/AVP 0 0".
    private const string DuplicatePayloadTypeSdp =
        "v=0\r\n" +
        "o=- 0 0 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "m=audio 40000 RTP/AVP 0 0\r\n" +
        "a=rtpmap:0 PCMU/8000\r\n";

    [Fact]
    public void Valid_sdp_parses_via_the_non_throwing_contract()
    {
        var parser = new SdpSessionParser();

        Assert.True(parser.TryParse(ValidSdp, out var result));
        Assert.NotNull(result);
        Assert.Single(result!.Media);
    }

    [Fact]
    public void Duplicate_payload_type_is_rejected_without_throwing()
    {
        var parser = new SdpSessionParser();

        Assert.False(parser.TryParse(DuplicatePayloadTypeSdp, out var result));
        Assert.Null(result);

        // Parse surfaces the malformation as a controlled FormatException — never the raw
        // ArgumentException the old ToDictionary path threw.
        Assert.Throws<FormatException>(() => parser.Parse(DuplicatePayloadTypeSdp));
    }

    [Fact]
    public void Extract_call_site_does_not_throw_on_a_duplicate_payload_type()
    {
        // End-to-end: the previously crash-vulnerable FormatException-only path returns cleanly.
        var (fingerprint, setup) = SdpUtilities.TryExtractAudioDtls(DuplicatePayloadTypeSdp);

        Assert.Null(fingerprint);
        Assert.Null(setup);
    }

    [Fact]
    public void Null_or_empty_input_is_rejected_without_throwing()
    {
        var parser = new SdpSessionParser();

        Assert.False(parser.TryParse(null, out _));
        Assert.False(parser.TryParse("   ", out _));
    }

    [Fact]
    public void A_body_over_the_size_cap_is_rejected()
    {
        var parser = new SdpSessionParser(new SdpParserLimits { MaxSdpBytes = 64 });

        Assert.False(parser.TryParse(ValidSdp, out _)); // ValidSdp is ~110 chars, over the 64 cap
    }

    [Fact]
    public void Too_many_lines_are_rejected()
    {
        var parser = new SdpSessionParser(new SdpParserLimits { MaxLines = 4 });

        Assert.False(parser.TryParse(ValidSdp, out _)); // ValidSdp has 7 lines
    }

    [Fact]
    public void A_line_over_the_line_cap_is_rejected()
    {
        var parser = new SdpSessionParser(new SdpParserLimits { MaxLineBytes = 100 });
        var sdp = ValidSdp + "a=" + new string('x', 200) + "\r\n";

        Assert.False(parser.TryParse(sdp, out _));
    }

    [Fact]
    public void Too_many_media_sections_are_rejected()
    {
        var parser = new SdpSessionParser(new SdpParserLimits { MaxMediaSections = 2 });
        var sdp =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40000 RTP/AVP 0\r\n" +
            "m=video 40002 RTP/AVP 96\r\n" +
            "m=audio 40004 RTP/AVP 0\r\n"; // third section exceeds the cap of 2

        Assert.False(parser.TryParse(sdp, out _));
    }

    [Fact]
    public void An_oversize_flood_body_is_rejected_by_the_size_cap()
    {
        // The size check short-circuits before the split/allocation, so a large hostile body is
        // bounded work rather than an out-of-memory: it simply returns false, quickly.
        var parser = new SdpSessionParser();
        var flood = "v=0\r\n" + new string('a', 400 * 1024);

        Assert.False(parser.TryParse(flood, out _));
    }
}
