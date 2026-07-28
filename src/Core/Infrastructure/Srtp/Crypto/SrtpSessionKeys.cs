using System.Security.Cryptography;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// Derived session keys for one SRTP context (RFC 3711 §4.3).
/// Generated from master key + master salt via the key derivation function.
/// </summary>
internal sealed class SrtpSessionKeys
{
    /// <summary>Session cipher key (16 or 32 bytes depending on suite).</summary>
    public required byte[] CipherKey { get; init; }

    /// <summary>Session salting key — 14 bytes for AES-CM, 12 bytes for AEAD-GCM (RFC 3711 §4.3 / RFC 7714 §8.1).</summary>
    public required byte[] Salt { get; init; }

    /// <summary>
    /// Session authentication key — 20 bytes for HMAC-SHA1 on the AES-CM suites (RFC 3711 §4.3).
    /// <see langword="null"/> for AEAD-GCM, which authenticates intrinsically and derives no
    /// separate auth key (RFC 7714 §11).
    /// </summary>
    public byte[]? AuthKey { get; init; }

    /// <summary>
    /// Overwrites all session key bytes with zeros (RFC 3711 §9.4 key hygiene). Called by
    /// the owning context on dispose so derived keys do not linger in the heap until GC.
    /// The master key/salt in <see cref="SrtpKeyMaterial"/> is caller-owned and not covered
    /// here — it originates from SDP text that lives in the heap anyway.
    /// </summary>
    public void Zero()
    {
        CryptographicOperations.ZeroMemory(CipherKey);
        CryptographicOperations.ZeroMemory(Salt);
        if (AuthKey is not null)
            CryptographicOperations.ZeroMemory(AuthKey);
    }
}
