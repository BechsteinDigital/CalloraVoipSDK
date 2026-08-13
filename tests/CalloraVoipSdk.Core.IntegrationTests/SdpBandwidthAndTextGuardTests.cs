using System.Linq;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P3-18 and P3-19. A description may carry several <c>b=</c> lines and one at session level;
/// keeping only the last media-level one silently discards limits the peer stated. And SDP is
/// line-oriented, so a configured value carrying CR/LF does not produce a malformed attribute — it
/// produces additional lines the caller never asked for.
/// </summary>
public sealed class SdpBandwidthAndTextGuardTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";
    private const string Audio = "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

    private static SdpSessionDescription Parse(string body)
    {
        Assert.True(new SdpSessionParser().TryParse(Header + body, out var parsed));
        return parsed!;
    }

    // ── P3-18: every b= line survives ────────────────────────────────────────

    [Fact]
    public void Several_bandwidth_lines_on_one_section_are_all_kept()
    {
        // AS is kbit/s, TIAS is bit/s (RFC 4566 §5.8, RFC 3890) — different statements, neither
        // replaces the other. A single field kept only the last.
        var parsed = Parse($"{Audio}b=AS:512\r\nb=TIAS:500000\r\n");

        Assert.Equal(2, parsed.Media[0].Bandwidths.Count);
        Assert.Equal(new[] { "AS", "TIAS" }, parsed.Media[0].Bandwidths.Select(b => b.Type));
        Assert.Equal(new[] { 512, 500000 }, parsed.Media[0].Bandwidths.Select(b => b.Value));
    }

    [Fact]
    public void The_singular_property_still_reports_the_first_line()
    {
        var parsed = Parse($"{Audio}b=AS:512\r\nb=TIAS:500000\r\n");

        Assert.Equal("AS", parsed.Media[0].Bandwidth?.Type);
        Assert.Equal(512, parsed.Media[0].Bandwidth?.Value);
    }

    [Fact]
    public void A_session_level_bandwidth_is_no_longer_dropped()
    {
        // It applies to every section that does not override it; the parser only looked inside media
        // sections, so this went on the floor entirely.
        var parsed = Parse($"b=AS:1024\r\n{Audio}");

        Assert.Single(parsed.Bandwidths);
        Assert.Equal("AS", parsed.Bandwidths[0].Type);
        Assert.Equal(1024, parsed.Bandwidths[0].Value);
    }

    [Fact]
    public void Session_and_media_bandwidth_are_kept_apart()
    {
        var parsed = Parse($"b=AS:1024\r\n{Audio}b=AS:256\r\n");

        Assert.Equal(1024, parsed.Bandwidths[0].Value);
        Assert.Equal(256, parsed.Media[0].Bandwidths[0].Value);
    }

    [Fact]
    public void Bandwidth_lines_survive_a_serialise_round_trip()
    {
        var parsed = Parse($"b=AS:1024\r\n{Audio}b=AS:512\r\nb=TIAS:500000\r\n");
        var text = new SdpSessionSerializer().Serialize(parsed);

        Assert.Contains("b=AS:1024", text, StringComparison.Ordinal);
        Assert.Contains("b=AS:512", text, StringComparison.Ordinal);
        Assert.Contains("b=TIAS:500000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_without_a_bandwidth_line_reports_none()
    {
        var parsed = Parse(Audio);

        Assert.Empty(parsed.Media[0].Bandwidths);
        Assert.Null(parsed.Media[0].Bandwidth);
        Assert.Empty(parsed.Bandwidths);
    }

    // ── P3-19: a configured value cannot open a new line ─────────────────────

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\0")]
    [InlineData("\t")]
    public void A_control_character_in_a_configured_field_is_refused(string injected)
    {
        Assert.False(SdpTextGuard.IsLineSafe($"track{injected}value"));
        Assert.Throws<FormatException>(() => SdpTextGuard.Line($"track{injected}value", "msid"));
    }

    [Theory]
    [InlineData("stream track")]
    [InlineData("6ac1e4b9-0b1a-4f4e-9f0a-6f6d9a0b1c2d")]
    [InlineData("stream:with:colons")]
    [InlineData("ünïcödé")]
    public void Ordinary_values_pass_unchanged(string value)
    {
        Assert.True(SdpTextGuard.IsLineSafe(value));
        Assert.Equal(value, SdpTextGuard.Line(value, "msid"));
    }

    [Fact]
    public void A_null_value_is_treated_as_empty_rather_than_throwing()
    {
        Assert.Equal(string.Empty, SdpTextGuard.Line(null, "mid"));
        Assert.True(SdpTextGuard.IsLineSafe(null));
    }

    [Fact]
    public void An_injected_msid_cannot_smuggle_a_crypto_line_into_the_offer()
    {
        // The concrete attack: a track id straight from an API request. Without the guard this writes
        // an a=crypto attribute the caller never asked for, and the peer cannot tell it apart from a
        // deliberate one. SIPSorcery appends such fields verbatim.
        var session = new SdpSessionDescription
        {
            OriginAddress = "192.0.2.1",
            ConnectionAddress = "192.0.2.1",
            SessionDirection = SdpMediaDirection.SendRecv,
            Media =
            [
                new SdpMediaDescription
                {
                    MediaType = "audio",
                    Port = 40000,
                    Profile = "RTP/AVP",
                    Direction = SdpMediaDirection.SendRecv,
                    Codecs = [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
                    Msid = new SdpMsid
                    {
                        StreamId = "stream\r\na=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:AAAA",
                        TrackId = "track",
                    },
                },
            ],
        };

        var ex = Assert.Throws<FormatException>(() => new SdpSessionSerializer().Serialize(session));
        Assert.Contains("msid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_description_serialises_unchanged()
    {
        // The guard must not alter ordinary output — it only refuses what cannot be written.
        var parsed = Parse($"{Audio}a=mid:0\r\n");
        var text = new SdpSessionSerializer().Serialize(parsed);

        Assert.Contains("a=mid:0", text, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:0 PCMU/8000", text, StringComparison.Ordinal);
    }
}
