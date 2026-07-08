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

    /// <summary>Session salting key — always 14 bytes (RFC 3711 §4.3).</summary>
    public required byte[] Salt { get; init; }

    /// <summary>Session authentication key — 20 bytes for HMAC-SHA1 (RFC 3711 §4.3).</summary>
    public required byte[] AuthKey { get; init; }

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
        CryptographicOperations.ZeroMemory(AuthKey);
    }
}
