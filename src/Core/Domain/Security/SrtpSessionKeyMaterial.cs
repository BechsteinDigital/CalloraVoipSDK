using System;

namespace CalloraVoipSdk.Core.Security;

/// <summary>
/// Immutable carrier for the SDES master keys negotiated for one SRTP call leg
/// (RFC 4568 §6.1). Directionality follows the RTP data flow of this SDK instance:
/// <list type="bullet">
///   <item><description><b>Local</b> = keys we use to protect the RTP we <i>send</i>
///   (advertised in our own <c>a=crypto</c>).</description></item>
///   <item><description><b>Remote</b> = keys we use to unprotect the RTP we <i>receive</i>
///   (taken from the far end's <c>a=crypto</c>).</description></item>
/// </list>
/// This is a domain value that intentionally holds secret material inline, following the
/// precedent already set by the ICE password fields on <see cref="Calls.CallMediaParameters"/>.
/// It is a pure data carrier; it performs no key derivation and no cryptographic operations.
/// </summary>
public sealed class SrtpSessionKeyMaterial
{
    /// <summary>
    /// Negotiated crypto suite that governs how these master keys are used
    /// (cipher, key length, auth tag length).
    /// </summary>
    public required SrtpCryptoSuiteKind Suite { get; init; }

    /// <summary>
    /// Master key we use to protect outbound RTP.
    /// 16 bytes for AES-128 suites, 32 bytes for AES-256 suites (RFC 3711 §3.2.1).
    /// </summary>
    public required ReadOnlyMemory<byte> LocalMasterKey { get; init; }

    /// <summary>
    /// Master salt we use to protect outbound RTP. Always 14 bytes (112 bit) (RFC 3711 §3.2.1).
    /// </summary>
    public required ReadOnlyMemory<byte> LocalMasterSalt { get; init; }

    /// <summary>
    /// Master key we use to unprotect inbound RTP.
    /// 16 bytes for AES-128 suites, 32 bytes for AES-256 suites (RFC 3711 §3.2.1).
    /// </summary>
    public required ReadOnlyMemory<byte> RemoteMasterKey { get; init; }

    /// <summary>
    /// Master salt we use to unprotect inbound RTP. Always 14 bytes (112 bit) (RFC 3711 §3.2.1).
    /// </summary>
    public required ReadOnlyMemory<byte> RemoteMasterSalt { get; init; }
}
