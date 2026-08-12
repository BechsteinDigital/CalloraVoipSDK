using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #157 P2-4: an SDES crypto line must not be answered when it asks for parameters this SDK does not
/// implement. The previous selection checked only for an <c>inline:</c> prefix and the parser kept
/// <c>Split('|')[0]</c>, so a lifetime and an MKI were silently dropped. With an MKI that is not
/// cosmetic: the peer prefixes it to every packet's authentication portion, we read those bytes as
/// ciphertext, and the negotiation reports success while media never decodes (RFC 4568 §6.1).
/// </summary>
public sealed class SdesKeyParamPolicyTests
{
    // Exactly 30 bytes once decoded = 16-byte master key + 14-byte master salt
    // (AES_CM_128_HMAC_SHA1_80, RFC 4568 §6.1).
    private const string KeySalt = "MDEyMzQ1Njc4OTAxMjM0NTAxMjM0NTY3ODkwMTIz";
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static SdpCryptoAttribute Crypto(int tag, string keyParams) => new()
    {
        Tag = tag,
        CryptoSuite = Suite,
        KeyParams = keyParams,
    };

    // ── grammar ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_bare_inline_key_parses_with_no_lifetime_and_no_mki()
    {
        Assert.True(SdesKeyParam.TryParse("inline:" + KeySalt, out var parsed));

        Assert.Equal(KeySalt, parsed.KeySalt);
        Assert.Null(parsed.Lifetime);
        Assert.Null(parsed.Mki);
    }

    [Theory]
    [InlineData("inline:" + KeySalt + "|2^31", "2^31")]
    [InlineData("inline:" + KeySalt + "|1048576", "1048576")]
    public void A_lifetime_is_recognised_as_a_lifetime(string keyParams, string expected)
    {
        Assert.True(SdesKeyParam.TryParse(keyParams, out var parsed));

        Assert.Equal(expected, parsed.Lifetime);
        Assert.Null(parsed.Mki);
    }

    [Theory]
    // MKI with and without a preceding lifetime — the colon is what distinguishes the field.
    [InlineData("inline:" + KeySalt + "|1:4")]
    [InlineData("inline:" + KeySalt + "|2^31|1:4")]
    public void An_mki_is_recognised_as_an_mki(string keyParams)
    {
        Assert.True(SdesKeyParam.TryParse(keyParams, out var parsed));

        Assert.NotNull(parsed.Mki);
    }

    [Theory]
    [InlineData("")]
    [InlineData("inline:")]                                  // empty key-salt
    [InlineData("uri:https://example.org/key")]              // a different key method
    [InlineData("inline:" + KeySalt + "|2^31|1:4|extra")]    // more fields than the grammar allows
    [InlineData("inline:" + KeySalt + "|")]                  // trailing separator, empty field
    [InlineData("inline:" + KeySalt + "|2^31|2^31")]         // two lifetimes
    [InlineData("inline:" + KeySalt + ";inline:" + KeySalt)] // an MKI key set, not a single key
    public void Unparseable_or_unsupported_key_params_are_refused(string keyParams)
    {
        Assert.False(SdesKeyParam.TryParse(keyParams, out _));
    }

    // ── selection policy ─────────────────────────────────────────────────────

    [Fact]
    public void A_crypto_line_offering_an_mki_is_not_answered()
    {
        // The only offered line asks for an MKI: no selection at all is the fail-closed outcome —
        // the caller then declines SRTP rather than negotiating a call that cannot decode.
        Assert.Null(SdesCryptoSelector.SelectAnswer([Crypto(1, "inline:" + KeySalt + "|2^31|1:4")]));
    }

    [Fact]
    public void A_later_mki_free_line_is_chosen_over_an_earlier_mki_line()
    {
        // Skipping is per line, not per offer: an offer that lists an MKI variant first and a plain
        // one second still negotiates — on the line we can actually honour.
        var selection = SdesCryptoSelector.SelectAnswer(
        [
            Crypto(1, "inline:" + KeySalt + "|1:4"),
            Crypto(2, "inline:" + KeySalt),
        ]);

        Assert.NotNull(selection);
        Assert.Equal(2, selection!.RemoteOffer.Tag);
        Assert.Equal(2, selection.LocalAnswer.Tag);   // the answer mirrors the chosen tag (RFC 4568 §5.1.2)
    }

    [Fact]
    public void A_lifetime_only_line_is_still_answered()
    {
        // Deliberately unchanged: a lifetime is extremely common on the wire (2^31 is the RFC 3711 §9.2
        // default) and, unlike an MKI, its presence alone does not break decoding. Honouring the offered
        // value as a send limit is separate follow-up work — see #157 P2-4.
        var selection = SdesCryptoSelector.SelectAnswer([Crypto(1, "inline:" + KeySalt + "|2^31")]);

        Assert.NotNull(selection);
        Assert.Equal(1, selection!.RemoteOffer.Tag);
    }

    // ── key material length ──────────────────────────────────────────────────

    [Fact]
    public void Inline_key_material_longer_than_the_suite_requires_is_refused()
    {
        // 32 bytes where the suite fixes 30 (16 + 14). Surplus bytes are not spare key material — they
        // mean the peer encoded something we are not reading the way it intended.
        var tooLong = Convert.ToBase64String(new byte[32]);

        var ex = Assert.Throws<FormatException>(
            () => SrtpKeyMaterial.ParseInline("inline:" + tooLong, SrtpCryptoSuite.AesCm128HmacSha1_80));

        Assert.Contains("exactly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_key_material_of_the_exact_suite_length_still_parses()
    {
        using var material = SrtpKeyMaterial.ParseInline("inline:" + KeySalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

        Assert.Equal(16, material.MasterKey.Length);
        Assert.Equal(14, material.MasterSalt.Length);
    }
}
