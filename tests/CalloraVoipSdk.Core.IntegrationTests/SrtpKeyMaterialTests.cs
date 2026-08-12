using System;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #10: the per-context SRTP <em>master</em> key material must be wiped once the
/// session keys have been derived — it must not linger on the managed heap for the SRTP context
/// lifetime. These tests pin the zeroing contract of <see cref="SrtpKeyMaterial"/> (both the DTLS
/// and SDES construction paths funnel through it) and that <see cref="SrtpKeyMaterial.ParseInline"/>
/// produces independent copies rather than views that alias the decoded staging buffer.
/// </summary>
public sealed class SrtpKeyMaterialTests
{
    private const int KeyLength = 16;  // AES-CM-128
    private const int SaltLength = 14; // RFC 3711 §3.2.1

    [Fact]
    public void Dispose_zeroes_the_master_key_and_salt_in_place()
    {
        var key = new byte[KeyLength];
        var salt = new byte[SaltLength];
        key.AsSpan().Fill(0xAB);
        salt.AsSpan().Fill(0xCD);

        var material = new SrtpKeyMaterial(key, salt, SrtpCryptoSuite.AesCm128HmacSha1_80);

        // The accessors alias the owned backing buffers, so before disposal they still carry the secret.
        Assert.True(material.MasterKey.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.True(material.MasterSalt.Span.IndexOfAnyExcept((byte)0) >= 0);

        material.Dispose();

        // After disposal both the passed-in arrays and the accessors read all-zero.
        Assert.True(key.AsSpan().IndexOfAnyExcept((byte)0) < 0, "master key was not wiped");
        Assert.True(salt.AsSpan().IndexOfAnyExcept((byte)0) < 0, "master salt was not wiped");
        Assert.True(material.MasterKey.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(material.MasterSalt.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var material = new SrtpKeyMaterial(new byte[KeyLength], new byte[SaltLength], SrtpCryptoSuite.AesCm128HmacSha1_80);

        material.Dispose();
        material.Dispose(); // must not throw
    }

    [Fact]
    public void ParseInline_copies_the_key_and_salt_out_of_the_decoded_buffer()
    {
        var raw = new byte[KeyLength + SaltLength];
        raw.AsSpan(0, KeyLength).Fill(0x11);
        raw.AsSpan(KeyLength, SaltLength).Fill(0x22);
        var keyParam = "inline:" + Convert.ToBase64String(raw);

        using var material = SrtpKeyMaterial.ParseInline(keyParam, SrtpCryptoSuite.AesCm128HmacSha1_80);

        Assert.True(material.MasterKey.Span.SequenceEqual(Repeat(0x11, KeyLength)));
        Assert.True(material.MasterSalt.Span.SequenceEqual(Repeat(0x22, SaltLength)));
    }

    [Fact]
    public void ParseInline_returns_independent_material_so_disposing_one_does_not_affect_another()
    {
        var raw = new byte[KeyLength + SaltLength];
        raw.AsSpan(0, KeyLength).Fill(0x11);
        raw.AsSpan(KeyLength, SaltLength).Fill(0x22);
        var keyParam = "inline:" + Convert.ToBase64String(raw);

        var first = SrtpKeyMaterial.ParseInline(keyParam, SrtpCryptoSuite.AesCm128HmacSha1_80);
        using var second = SrtpKeyMaterial.ParseInline(keyParam, SrtpCryptoSuite.AesCm128HmacSha1_80);

        first.Dispose();

        // Disposing the first instance wipes only its own copy; a second parse of the same string is unaffected,
        // which only holds if ParseInline hands out independent buffers (and does not leak a shared staging array).
        Assert.True(first.MasterKey.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(second.MasterKey.Span.SequenceEqual(Repeat(0x11, KeyLength)));
        Assert.True(second.MasterSalt.Span.SequenceEqual(Repeat(0x22, SaltLength)));
    }

    [Fact]
    public void A_key_param_with_a_bad_prefix_is_rejected_without_echoing_the_key_material()
    {
        // #157 P2-5: a malformed prefix does not make the rest harmless — it still carries base64 master
        // key material. Interpolating the whole key-param put it into a FormatException that reaches
        // generic error logs as an inner exception (K5: key material never appears in logs).
        var secret = Convert.ToBase64String(Repeat(0xAB, KeyLength + SaltLength));
        var keyParam = "inlin:" + secret;   // one character off — a realistic peer typo, not an attack

        var ex = Assert.Throws<FormatException>(
            () => SrtpKeyMaterial.ParseInline(keyParam, SrtpCryptoSuite.AesCm128HmacSha1_80));

        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(keyParam, ex.Message, StringComparison.Ordinal);
        // Still diagnosable: the message names the expected prefix and the observed length.
        Assert.Contains("inline:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(keyParam.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Repeat(byte value, int count)
    {
        var buffer = new byte[count];
        buffer.AsSpan().Fill(value);
        return buffer;
    }
}
