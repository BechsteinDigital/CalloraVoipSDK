using System.Linq;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-4 and P2-5: syntax is not the same as domain. A value can be well-formed text and still be
/// impossible on the wire — a payload type needs seven bits, a port sixteen, a DTLS setup role is one of
/// four tokens, and a fingerprint has to be a hash the peer's certificate can actually be measured with.
/// Accepting such values does not fail loudly; it fails later, somewhere quiet.
/// </summary>
public sealed class SdpWireDomainValidationTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";

    private static SdpSessionDescription? Parse(string sdp) =>
        new SdpSessionParser().TryParse(Header + sdp, out var parsed) ? parsed : null;

    // ── payload type: seven bits (RFC 3550 §5.1) ─────────────────────────────

    [Fact]
    public void A_payload_type_above_127_is_not_accepted_from_the_media_line()
    {
        // The review's probe: "m=audio … 256" was answered as RTP/AVP 256 and the value later cast to
        // byte — silently becoming 0, PCMU, a payload type nobody negotiated.
        var parsed = Parse("m=audio 40000 RTP/AVP 256\r\na=rtpmap:256 PCMU/8000\r\n");

        Assert.NotNull(parsed);
        Assert.DoesNotContain(parsed!.Media[0].Codecs, c => c.PayloadType == 256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(96)]
    [InlineData(127)]
    public void A_payload_type_within_the_seven_bit_range_is_accepted(int payloadType)
    {
        var parsed = Parse($"m=audio 40000 RTP/AVP {payloadType}\r\na=rtpmap:{payloadType} PCMU/8000\r\n");

        Assert.NotNull(parsed);
        Assert.Contains(parsed!.Media[0].Codecs, c => c.PayloadType == payloadType);
    }

    [Fact]
    public void An_out_of_range_payload_type_does_not_discard_the_valid_ones_beside_it()
    {
        // Skipping the impossible value must not cost the negotiable ones on the same m-line.
        var parsed = Parse("m=audio 40000 RTP/AVP 0 256 8\r\na=rtpmap:0 PCMU/8000\r\na=rtpmap:8 PCMA/8000\r\n");

        Assert.NotNull(parsed);
        Assert.Equal(new[] { 0, 8 }, parsed!.Media[0].Codecs.Select(c => c.PayloadType).OrderBy(pt => pt));
    }

    // ── port: sixteen bits (RFC 8866 §5.14) ──────────────────────────────────

    [Theory]
    [InlineData("70000")]
    [InlineData("-1")]
    public void A_port_outside_the_sixteen_bit_range_fails_the_parse(string port)
    {
        Assert.Null(Parse($"m=audio {port} RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"));
    }

    [Theory]
    [InlineData("0")]       // a declined m-line
    [InlineData("65535")]
    public void A_port_within_range_parses(string port)
    {
        Assert.NotNull(Parse($"m=audio {port} RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"));
    }

    // ── DTLS setup role: four tokens (RFC 4145 §4) ───────────────────────────

    [Fact]
    public void An_unknown_setup_role_leaves_the_role_unset()
    {
        // The review's probe: "a=setup:nonsense" produced an m-line carrying a role nobody agreed. Leaving
        // it unset is what the DTLS layer already fails closed on.
        var parsed = Parse("m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\na=setup:nonsense\r\n");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Media[0].DtlsSetup);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("passive")]
    [InlineData("actpass")]
    [InlineData("holdconn")]
    public void A_known_setup_role_is_kept(string role)
    {
        var parsed = Parse($"m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\na=setup:{role}\r\n");

        Assert.Equal(role, parsed?.Media[0].DtlsSetup);
    }

    // ── fingerprint grammar (RFC 8122 §5) ────────────────────────────────────

    [Theory]
    [InlineData("garbage nope")]          // the review's probe: neither a hash function nor hex
    [InlineData("sha-256 not-hex-at-all")]
    [InlineData("sha-256 AA:BB:")]        // trailing separator
    [InlineData("sha-256 AABBCC")]        // no separators
    [InlineData("sha-999 AA:BB:CC")]      // hash function that cannot be computed
    public void A_fingerprint_that_cannot_be_one_is_rejected(string attrValue)
    {
        Assert.Null(SdpFingerprint.TryParse(attrValue));
    }

    [Theory]
    [InlineData("sha-256", "AA:BB:CC")]
    [InlineData("sha-1", "aa:bb:cc:dd")]   // lowercase hex is equally valid
    [InlineData("SHA-256", "AA:BB")]       // the algorithm token is case-insensitive
    public void A_well_formed_fingerprint_is_accepted(string algorithm, string value)
    {
        var parsed = SdpFingerprint.TryParse($"{algorithm} {value}");

        Assert.Equal(algorithm, parsed?.Algorithm);
        Assert.Equal(value, parsed?.Value);
    }

    [Fact]
    public void A_malformed_fingerprint_leaves_the_m_line_without_one()
    {
        // The consequence that matters: no fingerprint means nothing claims the leg is authenticated.
        var parsed = Parse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\na=fingerprint:garbage nope\r\n");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Media[0].Fingerprint);
    }
}
