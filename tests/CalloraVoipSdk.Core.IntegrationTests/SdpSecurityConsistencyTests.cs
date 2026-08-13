using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-15 and P2-17: two ways the security reading of a description depended on where a line sat
/// rather than on what it said — on the position of an m-line, and on the order of attributes inside
/// one. Both let a peer be understood differently by two implementations looking at the same bytes.
/// </summary>
public sealed class SdpSecurityConsistencyTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";

    private static bool TryParse(string body) =>
        new SdpSessionParser().TryParse(Header + body, out _);

    private static SdpSessionDescription Parse(string body)
    {
        Assert.True(new SdpSessionParser().TryParse(Header + body, out var parsed));
        return parsed!;
    }

    // ── P2-17: every active audio m-line counts, not just the first ──────────

    private const string SecureAudio =
        "m=audio 40000 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
        "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:d0RmdmcmVCspeEc3QGZiNWpVLFJhQX1cfHAwJSoj\r\n";

    private const string PlainAudio =
        "m=audio 40002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

    [Fact]
    public void A_plain_audio_section_behind_a_secure_one_is_not_reported_as_srtp()
    {
        // The review's probe, and the reason it matters: SipCallChannelSrtpPolicyGuard turns this bool
        // into an accept/488 decision. Inspecting only the first section meant an offer could carry an
        // unencrypted audio stream past SrtpPolicy.Required by putting a secure m-line in front of it.
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            Header + SecureAudio + PlainAudio, out var isSrtpSignaled, out var profile);

        Assert.True(inspected);
        Assert.False(isSrtpSignaled);
        Assert.Equal("RTP/AVP", profile);   // the profile named is the weak leg, not the secure sibling
    }

    [Fact]
    public void A_plain_audio_section_in_front_is_still_not_reported_as_srtp()
    {
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            Header + PlainAudio + SecureAudio, out var isSrtpSignaled, out _);

        Assert.True(inspected);
        Assert.False(isSrtpSignaled);
    }

    [Fact]
    public void All_secure_audio_sections_are_reported_as_srtp()
    {
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            Header + SecureAudio + SecureAudio.Replace("40000", "40004", StringComparison.Ordinal),
            out var isSrtpSignaled,
            out var profile);

        Assert.True(inspected);
        Assert.True(isSrtpSignaled);
        Assert.Equal("RTP/SAVP", profile);
    }

    [Fact]
    public void A_declined_plain_section_does_not_weaken_the_signal()
    {
        // A zero-port m-line carries no media, so it cannot carry unencrypted media either.
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            Header + SecureAudio + "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n",
            out var isSrtpSignaled,
            out _);

        Assert.True(inspected);
        Assert.True(isSrtpSignaled);
    }

    [Fact]
    public void A_single_secure_audio_section_is_unchanged()
    {
        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            Header + SecureAudio, out var isSrtpSignaled, out var profile);

        Assert.True(inspected);
        Assert.True(isSrtpSignaled);
        Assert.Equal("RTP/SAVP", profile);
    }

    // ── P2-15: a contradiction is a parse failure, not a last-wins decision ──

    [Fact]
    public void Two_fingerprints_for_the_same_hash_function_are_rejected()
    {
        // The fingerprint is the only thing authenticating the DTLS peer (RFC 5763 §6.7.1). A last-wins
        // parser reads the second, a first-wins parser the first — so two endpoints can believe they
        // agreed on different certificates while looking at identical bytes.
        Assert.False(TryParse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=fingerprint:sha-256 AA:BB:CC\r\n" +
            "a=fingerprint:sha-256 DD:EE:FF\r\n"));
    }

    [Fact]
    public void Fingerprints_for_different_hash_functions_are_kept_and_the_first_wins()
    {
        // Legal: the same certificate measured more than one way (RFC 8122 §5). Which one we act on
        // must not depend on line order either, so the first is kept rather than the last.
        var parsed = Parse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=fingerprint:sha-256 AA:BB:CC\r\n" +
            "a=fingerprint:sha-1 DD:EE:FF\r\n");

        Assert.Equal("sha-256", parsed.Media[0].Fingerprint?.Algorithm);
        Assert.Equal("AA:BB:CC", parsed.Media[0].Fingerprint?.Value);
    }

    [Fact]
    public void An_exactly_repeated_fingerprint_is_accepted()
    {
        // A duplicate says nothing new, and implementations do emit them. Only a contradiction fails.
        var parsed = Parse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=fingerprint:sha-256 AA:BB:CC\r\n" +
            "a=fingerprint:sha-256 AA:BB:CC\r\n");

        Assert.Equal("AA:BB:CC", parsed.Media[0].Fingerprint?.Value);
    }

    [Fact]
    public void Two_different_setup_roles_are_rejected()
    {
        // a=setup decides who runs the DTLS handshake as client (RFC 4145 §4) — a question two peers
        // must not answer differently.
        Assert.False(TryParse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=setup:passive\r\na=setup:active\r\n"));
    }

    [Fact]
    public void A_repeated_identical_setup_role_is_accepted()
    {
        var parsed = Parse(
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=setup:actpass\r\na=setup:actpass\r\n");

        Assert.Equal("actpass", parsed.Media[0].DtlsSetup);
    }

    [Fact]
    public void Two_different_directions_on_one_m_line_are_rejected()
    {
        // "sendrecv" then "inactive" has no defined meaning, and picking one is this parser's opinion
        // rather than the peer's statement.
        Assert.False(TryParse(
            "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\na=inactive\r\n"));
    }

    [Fact]
    public void A_repeated_identical_direction_is_accepted()
    {
        var parsed = Parse(
            "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\na=sendonly\r\n");

        Assert.Equal(SdpMediaDirection.SendOnly, parsed.Media[0].Direction);
    }

    [Fact]
    public void Two_different_ice_credentials_are_rejected()
    {
        // The short-term credential decides which STUN checks authenticate (RFC 8839 §5.4).
        Assert.False(TryParse(
            "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=ice-ufrag:aaaa\r\na=ice-ufrag:bbbb\r\n"));

        Assert.False(TryParse(
            "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=ice-pwd:0123456789abcdefghijkl\r\na=ice-pwd:mnopqrstuvwxyz0123456789\r\n"));
    }

    [Fact]
    public void Two_different_mids_on_one_m_line_are_rejected()
    {
        // The mid is the 1:1 handle offer and answer are matched by (RFC 8829 §5.3.1); a section with
        // two of them has no identity at all.
        Assert.False(TryParse(
            "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\na=mid:1\r\n"));
    }

    [Fact]
    public void The_guard_is_per_level_not_per_description()
    {
        // Each m-line has its own scope: two sections legitimately carry different mids, different
        // directions and different setup roles. Guarding globally would reject ordinary SDP.
        var parsed = Parse(
            "a=sendrecv\r\n" +
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=mid:0\r\na=sendonly\r\na=setup:active\r\na=fingerprint:sha-256 AA:BB:CC\r\n" +
            "m=video 40002 UDP/TLS/RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n" +
            "a=mid:1\r\na=recvonly\r\na=setup:passive\r\na=fingerprint:sha-256 DD:EE:FF\r\n");

        Assert.Equal("0", parsed.Media[0].Mid);
        Assert.Equal("1", parsed.Media[1].Mid);
        Assert.Equal(SdpMediaDirection.SendOnly, parsed.Media[0].Direction);
        Assert.Equal(SdpMediaDirection.RecvOnly, parsed.Media[1].Direction);
        Assert.Equal("active", parsed.Media[0].DtlsSetup);
        Assert.Equal("passive", parsed.Media[1].DtlsSetup);
        Assert.Equal("AA:BB:CC", parsed.Media[0].Fingerprint?.Value);
        Assert.Equal("DD:EE:FF", parsed.Media[1].Fingerprint?.Value);
    }

    [Fact]
    public void A_media_level_attribute_may_differ_from_the_session_level_one()
    {
        // Session level and media level are separate scopes: a media attribute overrides rather than
        // contradicts (RFC 8866 §5.13).
        var parsed = Parse(
            "a=setup:actpass\r\na=fingerprint:sha-256 AA:BB:CC\r\n" +
            "m=audio 40000 UDP/TLS/RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "a=setup:active\r\na=fingerprint:sha-256 DD:EE:FF\r\n");

        Assert.Equal("active", parsed.Media[0].DtlsSetup);
        Assert.Equal("DD:EE:FF", parsed.Media[0].Fingerprint?.Value);
    }
}
