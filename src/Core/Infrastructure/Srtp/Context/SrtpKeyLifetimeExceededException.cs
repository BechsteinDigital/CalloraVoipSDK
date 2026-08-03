namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Thrown on the send path when protecting another packet would exhaust the current key's usable
/// index space (RFC 3711 §9.2): at most 2^48 SRTP packets or 2^31 SRTCP packets may be protected
/// under one master key. Beyond that the extended packet index / 31-bit SRTCP index would wrap and
/// reuse the AES-CM keystream or the AEAD-GCM nonce (RFC 7714 §11) under the same key — a
/// catastrophic confidentiality failure. The sender fails closed with this exception instead of
/// emitting a reused-keystream packet; recovery requires a fresh key (live rekey is tracked
/// separately). Callers must suppress the packet — never fall back to an unprotected send.
/// </summary>
internal sealed class SrtpKeyLifetimeExceededException : Exception
{
    public SrtpKeyLifetimeExceededException(string message) : base(message) { }
}
