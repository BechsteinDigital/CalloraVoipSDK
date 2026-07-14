using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// SRTP master key material derived from a completed DTLS-SRTP handshake via the
/// <c>EXTRACTOR-dtls_srtp</c> keying-material exporter (RFC 5764 §4.2), already split
/// into the local (outbound-protect) and remote (inbound-unprotect) halves for the
/// negotiated protection profile.
/// </summary>
internal sealed class DtlsSrtpNegotiatedKeys
{
    /// <summary>Crypto suite corresponding to the negotiated <c>use_srtp</c> profile.</summary>
    public required SrtpCryptoSuite Suite { get; init; }

    /// <summary>Master key/salt protecting packets this endpoint sends.</summary>
    public required SrtpKeyMaterial LocalKeys { get; init; }

    /// <summary>Master key/salt un-protecting packets this endpoint receives.</summary>
    public required SrtpKeyMaterial RemoteKeys { get; init; }
}
