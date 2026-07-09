using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// SRTP key derivation function (RFC 3711 §4.3).
/// Derives cipher key, salt, and auth key from master key + master salt
/// using AES-128/256 Counter Mode as a PRF (PRF-128 / PRF-256).
/// </summary>
internal static class SrtpKeyDerivation
{
    // Label constants (RFC 3711 §4.3.1). SRTP uses 0/1/2, SRTCP uses 3/4/5 — deriving
    // SRTCP keys from the same master key with distinct labels keeps the two keystreams
    // independent (RFC 3711 §4.3.2).
    private const byte LabelCipherKey = 0x00;
    private const byte LabelAuthKey   = 0x01;
    private const byte LabelSalt      = 0x02;
    private const byte LabelRtcpCipherKey = 0x03;
    private const byte LabelRtcpAuthKey   = 0x04;
    private const byte LabelRtcpSalt      = 0x05;

    /// <summary>
    /// Derives the SRTP session keys for the given master key material.
    /// Key derivation rate r = 0 (default — keys derived once per session).
    /// </summary>
    public static SrtpSessionKeys Derive(SrtpKeyMaterial material) =>
        Derive(material, LabelCipherKey, LabelAuthKey, LabelSalt);

    /// <summary>
    /// Derives the SRTCP session keys (RFC 3711 §4.3.2, labels 3/4/5) from the same master
    /// key material as <see cref="Derive(SrtpKeyMaterial)"/>, yielding an independent
    /// keystream for RTCP.
    /// </summary>
    public static SrtpSessionKeys DeriveRtcp(SrtpKeyMaterial material) =>
        Derive(material, LabelRtcpCipherKey, LabelRtcpAuthKey, LabelRtcpSalt);

    private static SrtpSessionKeys Derive(
        SrtpKeyMaterial material, byte cipherLabel, byte authLabel, byte saltLabel)
    {
        ArgumentNullException.ThrowIfNull(material);

        var keyLength  = material.MasterKey.Length; // 16 or 32
        var authLength = 20; // HMAC-SHA1 key = 20 bytes

        return new SrtpSessionKeys
        {
            CipherKey = DeriveKey(material, cipherLabel, keyLength),
            Salt      = DeriveKey(material, saltLabel,   14),
            AuthKey   = DeriveKey(material, authLabel,   authLength),
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
