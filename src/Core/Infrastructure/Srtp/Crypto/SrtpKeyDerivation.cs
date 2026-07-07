using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// SRTP key derivation function (RFC 3711 §4.3).
/// Derives cipher key, salt, and auth key from master key + master salt
/// using AES-128/256 Counter Mode as a PRF (PRF-128 / PRF-256).
/// </summary>
internal static class SrtpKeyDerivation
{
    // Label constants (RFC 3711 §4.3.1). Labels 0/1/2 derive the SRTP session keys,
    // labels 3/4/5 derive the SRTCP session keys from the SAME master key + master salt.
    private const byte LabelCipherKey      = 0x00;
    private const byte LabelAuthKey        = 0x01;
    private const byte LabelSalt           = 0x02;
    private const byte LabelSrtcpCipherKey = 0x03;
    private const byte LabelSrtcpAuthKey   = 0x04;
    private const byte LabelSrtcpSalt      = 0x05;

    // HMAC-SHA1 authentication key length (RFC 3711 §4.3) — same for SRTP and SRTCP.
    private const int AuthKeyLength   = 20;
    private const int SessionSaltLength = 14;

    /// <summary>
    /// Derives all SRTP session keys for the given master key material (labels 0/1/2).
    /// Key derivation rate r = 0 (default — keys derived once per session).
    /// </summary>
    public static SrtpSessionKeys Derive(SrtpKeyMaterial material)
        => DeriveWithLabels(material, LabelCipherKey, LabelAuthKey, LabelSalt);

    /// <summary>
    /// Derives all SRTCP session keys for the given master key material (labels 3/4/5).
    /// Uses the same AES-CM PRF and the same master key/salt as <see cref="Derive"/>,
    /// only the key-derivation labels differ (RFC 3711 §4.3.2).
    /// </summary>
    public static SrtpSessionKeys DeriveSrtcp(SrtpKeyMaterial material)
        => DeriveWithLabels(material, LabelSrtcpCipherKey, LabelSrtcpAuthKey, LabelSrtcpSalt);

    // Shared derivation body so SRTP and SRTCP keys go through one PRF implementation.
    private static SrtpSessionKeys DeriveWithLabels(
        SrtpKeyMaterial material,
        byte cipherLabel,
        byte authLabel,
        byte saltLabel)
    {
        ArgumentNullException.ThrowIfNull(material);

        var keyLength = material.MasterKey.Length; // 16 or 32

        return new SrtpSessionKeys
        {
            CipherKey = DeriveKey(material, cipherLabel, keyLength),
            Salt      = DeriveKey(material, saltLabel,   SessionSaltLength),
            AuthKey   = DeriveKey(material, authLabel,   AuthKeyLength),
        };
    }

    // -------------------------------------------------------------------------
    // PRF (AES Counter Mode as pseudo-random function, RFC 3711 §4.3.1)
    // -------------------------------------------------------------------------

    private static byte[] DeriveKey(SrtpKeyMaterial material, byte label, int outputLength)
    {
        var iv = new byte[16];
        var salt = material.MasterSalt.Span;

        // x = (label || index_div_kdr) XOR master_salt, then x * 2^16.
        salt.CopyTo(iv);

        // label is the first byte of the 7-octet key_id (label || 6 zero octets).
        iv[7] ^= label;

        return AesCmPrf(material.MasterKey.Span, iv, outputLength);
    }

    /// <summary>
    /// AES Counter Mode PRF — generates <paramref name="outputLength"/> bytes of keystream
    /// by encrypting successive counter blocks (IV, IV+1, IV+2, …) with AES-ECB.
    /// </summary>
    private static byte[] AesCmPrf(ReadOnlySpan<byte> key, byte[] iv, int outputLength)
    {
        var output  = new byte[outputLength];
        var written = 0;
        var counter = 0;

        using var aes = Aes.Create();
        aes.Key     = key.ToArray();
        aes.Mode    = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var block = new byte[16];
        var counterIv = (byte[])iv.Clone();

        while (written < outputLength)
        {
            counterIv[14] = (byte)(counter >> 8);
            counterIv[15] = (byte)counter;

            encryptor.TransformBlock(counterIv, 0, 16, block, 0);

            var toCopy = Math.Min(16, outputLength - written);
            block.AsSpan(0, toCopy).CopyTo(output.AsSpan(written));
            written += toCopy;

            counter++;
        }

        return output;
    }
}
