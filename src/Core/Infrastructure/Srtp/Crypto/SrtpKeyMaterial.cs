using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// Master key and salt for one SRTP crypto context (RFC 3711 §3.2.1).
/// Passed in from SDP SDES negotiation (RFC 4568 §6.1) or exported from a DTLS-SRTP
/// handshake (RFC 5764 §4.2).
/// </summary>
/// <remarks>
/// Owns the two backing buffers holding the master key and salt in the clear, so a consumer
/// can wipe them (<see cref="Dispose"/>) once the session keys have been derived (RFC 3711 §4.3)
/// and the master material is no longer needed — there is no per-session re-keying in this SDK.
/// <see cref="MasterKey"/>/<see cref="MasterSalt"/> alias those buffers and therefore read as
/// all-zero after disposal; derive before you dispose.
/// </remarks>
internal sealed class SrtpKeyMaterial : IDisposable
{
    private readonly byte[] _masterKey;
    private readonly byte[] _masterSalt;
    private bool _disposed;

    /// <summary>
    /// Creates key material that takes ownership of <paramref name="masterKey"/> and
    /// <paramref name="masterSalt"/> — the arrays are wiped in place on <see cref="Dispose"/>,
    /// so the caller must not reuse or retain them elsewhere.
    /// </summary>
    /// <param name="masterKey">Master key — 16 bytes for AES-128 suites, 32 for AES-256 (RFC 3711 §3.2.1).</param>
    /// <param name="masterSalt">Master salt — 14 bytes for AES-CM (RFC 3711 §3.2.1), 12 bytes for AEAD-GCM (RFC 7714 §8.1).</param>
    /// <param name="suite">Crypto suite that determines how this key material is used.</param>
    public SrtpKeyMaterial(byte[] masterKey, byte[] masterSalt, SrtpCryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentNullException.ThrowIfNull(masterSalt);
        _masterKey = masterKey;
        _masterSalt = masterSalt;
        Suite = suite;
    }

    /// <summary>
    /// Master key — 16 bytes for AES-128 suites, 32 bytes for AES-256 suites (RFC 3711 §3.2.1).
    /// Reads all-zero after <see cref="Dispose"/>.
    /// </summary>
    public ReadOnlyMemory<byte> MasterKey => _masterKey;

    /// <summary>
    /// Master salt — 14 bytes for AES-CM (RFC 3711 §3.2.1), 12 bytes for AEAD-GCM (RFC 7714 §8.1).
    /// Reads all-zero after <see cref="Dispose"/>.
    /// </summary>
    public ReadOnlyMemory<byte> MasterSalt => _masterSalt;

    /// <summary>
    /// Crypto suite that determines how this key material is used.
    /// </summary>
    public SrtpCryptoSuite Suite { get; }

    /// <summary>
    /// Parses a base64-encoded SDES key-param string into key material.
    /// Format: "inline:&lt;base64(key+salt)&gt;" (RFC 4568 §6.1).
    /// </summary>
    public static SrtpKeyMaterial ParseInline(string keyParam, SrtpCryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(keyParam);

        const string prefix = "inline:";
        if (!keyParam.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            // #157 P2-5: never interpolate the key-param itself. A malformed prefix does not make the
            // rest of the string harmless — it still carries base64 master key material, and as an inner
            // exception it would reach generic error logs. The length is enough to diagnose the shape.
            throw new FormatException(
                $"SRTP key-param must start with 'inline:' (RFC 4568 §6.1); got {keyParam.Length} characters with a different prefix.");
        }

        var raw = Convert.FromBase64String(keyParam[prefix.Length..].Split('|')[0]);

        try
        {
            var keyLength = SrtpCryptoSuiteNames.KeyLength(suite);
            var saltLength = SrtpCryptoSuiteNames.SaltLength(suite); // 14 for AES-CM, 12 for AEAD-GCM

            if (raw.Length < keyLength + saltLength)
                throw new FormatException(
                    $"SRTP inline key too short: {raw.Length} bytes, expected at least {keyLength + saltLength}.");

            // Copy the key and salt into their own owned buffers so the plaintext master material can
            // be wiped when this instance is disposed, then wipe the base64-decoded staging array below.
            return new SrtpKeyMaterial(
                raw[..keyLength].ToArray(),
                raw[keyLength..(keyLength + saltLength)].ToArray(),
                suite);
        }
        finally
        {
            // The decoded staging array held the master key + salt in the clear; wipe it once the
            // owned copies (or the exception) leave — never let it linger on the managed heap.
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>
    /// Zeroes the master key and salt in place (RFC 3711 §9.4 key hygiene). Idempotent. Call only
    /// after the session keys have been derived — the accessors read all-zero afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_masterKey);
        CryptographicOperations.ZeroMemory(_masterSalt);
    }
}
