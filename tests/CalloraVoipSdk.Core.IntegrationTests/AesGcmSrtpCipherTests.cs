using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 7714 §16 known-answer test for AEAD_AES_128_GCM SRTP. The vector publishes the <em>session</em>
/// encryption key and salt directly (the RFC 3711 KDF is not exercised here), so they feed straight into
/// the cipher. Verifies the cipher reproduces the published SRTP packet byte-for-byte — header as
/// clear-text AAD, payload encrypted, full 16-byte tag — plus a decrypt round-trip and tamper rejection.
/// Passing this proves the §8.1 IV construction, AAD scope and tag length are correct.
/// </summary>
public sealed class AesGcmSrtpCipherTests
{
    // RFC 7714 §16 vector (each 4-octet group copied verbatim to avoid transcription errors).
    private static readonly byte[] SessionKey  = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] SessionSalt = Convert.FromHexString("517569642070726f2071756f"); // 12 bytes
    private const uint Ssrc = 0x5501a0b2;
    private const uint Roc = 0;
    private const ushort Seq = 0xf17b;

    // 12-byte RTP header (AAD).
    private static readonly byte[] Header = Convert.FromHexString("8040f17b8041f8d35501a0b2");

    // 38-byte payload "Gallia est omnis divisa in partes tres".
    private static readonly byte[] Payload = Convert.FromHexString(
        "47616c6c" + "69612065" + "7374206f" + "6d6e6973" + "20646976" +
        "69736120" + "696e2070" + "61727465" + "73207472" + "6573");

    // Published SRTP output after the 12-byte clear header: 38-byte ciphertext + 16-byte tag.
    private static readonly byte[] ExpectedCiphertext = Convert.FromHexString(
        "f24de3a3" + "fb34de6c" + "acba861c" + "9d7e4bca" + "be633bd5" +
        "0d294e6f" + "42a5f47a" + "51c7d19b" + "36de3adf" + "8833");
    private static readonly byte[] ExpectedTag = Convert.FromHexString(
        "899d" + "7f27beb1" + "6a9152cf" + "765ee439" + "0cce");

    private static AesGcmSrtpCipher NewCipher() =>
        new(new SrtpSessionKeys
        {
            CipherKey = (byte[])SessionKey.Clone(),
            Salt = (byte[])SessionSalt.Clone(),
            AuthKey = null, // AEAD authenticates intrinsically (RFC 7714 §11)
        });

    [Fact]
    public void Encrypt_reproduces_the_published_ciphertext_and_tag()
    {
        using var cipher = NewCipher();
        var ciphertext = new byte[Payload.Length];
        var tag = new byte[AesGcmSrtpCipher.TagLength];

        cipher.Encrypt(Ssrc, Roc, Seq, Header, Payload, ciphertext, tag);

        Assert.Equal(ExpectedCiphertext, ciphertext);
        Assert.Equal(ExpectedTag, tag);
    }

    [Fact]
    public void Decrypt_round_trips_the_published_vector()
    {
        using var cipher = NewCipher();
        var plaintext = new byte[ExpectedCiphertext.Length];

        cipher.Decrypt(Ssrc, Roc, Seq, Header, ExpectedCiphertext, ExpectedTag, plaintext);

        Assert.Equal(Payload, plaintext);
    }

    [Fact]
    public void Decrypt_rejects_a_tampered_tag()
    {
        using var cipher = NewCipher();
        var badTag = (byte[])ExpectedTag.Clone();
        badTag[0] ^= 0xFF;
        var plaintext = new byte[ExpectedCiphertext.Length];

        Assert.Throws<AuthenticationTagMismatchException>(
            () => cipher.Decrypt(Ssrc, Roc, Seq, Header, ExpectedCiphertext, badTag, plaintext));
    }

    [Fact]
    public void Kdf_derives_usable_gcm_session_keys_and_round_trips()
    {
        // Exercises the GCM branch of the RFC 3711 §4.3 KDF: 12-byte salt (RFC 7714 §8.1), no separate
        // auth key (§11). Proves the derived key/salt are the right shape and actually usable end-to-end.
        var masterKey = RandomNumberGenerator.GetBytes(16);
        var masterSalt = RandomNumberGenerator.GetBytes(12);
        using var material = new SrtpKeyMaterial(masterKey, masterSalt, SrtpCryptoSuite.AeadAes128Gcm);

        var keys = SrtpKeyDerivation.Derive(material);
        Assert.Equal(16, keys.CipherKey.Length);
        Assert.Equal(12, keys.Salt.Length);
        Assert.Null(keys.AuthKey);

        using var cipher = new AesGcmSrtpCipher(keys);
        var payload = "hello srtp gcm payload"u8.ToArray();
        var header = Convert.FromHexString("8040000112345678abcdef01");
        var ciphertext = new byte[payload.Length];
        var tag = new byte[AesGcmSrtpCipher.TagLength];
        cipher.Encrypt(0xabcdef01, 0, 1, header, payload, ciphertext, tag);

        var roundTripped = new byte[payload.Length];
        cipher.Decrypt(0xabcdef01, 0, 1, header, ciphertext, tag, roundTripped);
        Assert.Equal(payload, roundTripped);
    }
}
