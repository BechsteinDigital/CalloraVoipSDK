using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-046 SIP wire robustness (RFC 3261 §7.3.1/§25.1): the comma-separated header-value split preserves commas
/// inside a name-addr's angle brackets, and dialog-tag extraction tolerates the linear white space (LWS) that
/// may surround the ";"/"tag"/"=" of a header parameter.
/// </summary>
public sealed class SipWireRobustnessTests
{
    // ── ExtractTag: LWS tolerance + bracket awareness ────────────────────────────

    [Theory]
    [InlineData("<sip:a@b>;tag=abc123", "abc123")]
    [InlineData("<sip:a@b>; tag = abc123", "abc123")]          // LWS around ";", "tag", "="
    [InlineData("<sip:a@b>;tag =abc123", "abc123")]
    [InlineData("<sip:a@b>;TAG=abc123", "abc123")]             // case-insensitive parameter name
    [InlineData("\"Bob\" <sip:a@b>;tag=xyz;other=1", "xyz")]   // tag before another parameter
    [InlineData("<sip:a@b>;other=1; tag = xyz", "xyz")]        // tag after another parameter
    public void ExtractTag_reads_the_dialog_tag_tolerating_lws(string header, string expected)
    {
        Assert.Equal(expected, SipProtocol.ExtractTag(header));
    }

    [Theory]
    [InlineData("<sip:a@b>")]                    // no tag parameter
    [InlineData("<sip:a@b;tag=uriparam>")]       // ;tag= inside the <URI> is a URI parameter, not the dialog tag
    [InlineData("<sip:a@b>;tag=")]               // empty tag value
    [InlineData(null)]
    [InlineData("")]
    public void ExtractTag_returns_null_when_there_is_no_header_tag(string? header)
    {
        Assert.Null(SipProtocol.ExtractTag(header));
    }

    [Fact]
    public void ExtractTag_prefers_the_header_tag_over_a_uri_parameter_named_tag()
    {
        Assert.Equal("real", SipProtocol.ExtractTag("<sip:a@b;tag=uri>;tag=real"));
    }

    // ── SplitCommaSeparatedRespectingQuotes: bracket + quote awareness ───────────

    [Fact]
    public void Split_keeps_commas_inside_angle_brackets_together()
    {
        var parts = ProtocolCommonUtilities
            .SplitCommaSeparatedRespectingQuotes("<sip:a@b;method=INVITE?to=x,y>, <sip:c@d>")
            .ToArray();

        Assert.Equal(2, parts.Length);
        Assert.Equal("<sip:a@b;method=INVITE?to=x,y>", parts[0]);
        Assert.Equal("<sip:c@d>", parts[1]);
    }

    [Fact]
    public void Split_keeps_commas_inside_quotes_together()
    {
        var parts = ProtocolCommonUtilities
            .SplitCommaSeparatedRespectingQuotes("\"Doe, Jane\" <sip:a@b>, \"Roe, John\" <sip:c@d>")
            .ToArray();

        Assert.Equal(2, parts.Length);
        Assert.Equal("\"Doe, Jane\" <sip:a@b>", parts[0]);
        Assert.Equal("\"Roe, John\" <sip:c@d>", parts[1]);
    }

    [Fact]
    public void Split_separates_a_plain_comma_list()
    {
        var parts = ProtocolCommonUtilities
            .SplitCommaSeparatedRespectingQuotes("<sip:a@b>, <sip:c@d>, <sip:e@f>")
            .ToArray();

        Assert.Equal(["<sip:a@b>", "<sip:c@d>", "<sip:e@f>"], parts);
    }
}
