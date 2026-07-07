using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// Result of SDP offer/answer negotiation.
/// </summary>
internal sealed class SdpOfferAnswerResult
{
    /// <summary>
    /// True when negotiation produced a compatible answer.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Negotiated answer model when success is true.
    /// </summary>
    public SdpSessionDescription? Answer { get; init; }

    /// <summary>
    /// Negotiated codec list for primary media stream.
    /// </summary>
    public IReadOnlyList<SdpCodecDefinition> NegotiatedCodecs { get; init; } = [];

    // -------------------------------------------------------------------------
    // Negotiated media parameters — convenience shortcuts for the media layer
    // -------------------------------------------------------------------------

    /// <summary>
    /// True when <c>a=rtcp-mux</c> was negotiated (RFC 5761).
    /// </summary>
    public bool RtcpMuxNegotiated { get; init; }

    /// <summary>
    /// DTLS fingerprint from the remote offer (RFC 8122).
    /// <see langword="null"/> when the remote side did not include a fingerprint.
    /// </summary>
    public SdpFingerprint? RemoteFingerprint { get; init; }

    /// <summary>
    /// DTLS setup role from the remote offer (RFC 4145).
    /// One of <c>actpass</c>, <c>active</c>, <c>passive</c>, or <c>holdconn</c>.
    /// <see langword="null"/> when not present in remote offer.
    /// </summary>
    public string? RemoteDtlsSetup { get; init; }

    /// <summary>
    /// Remote SDES crypto line accepted from the peer (RFC 4568). This carries the far end's
    /// master key, used to unprotect the RTP we receive.
    /// <see langword="null"/> when SDES was not offered or when DTLS is used instead.
    /// </summary>
    public SdpCryptoAttribute? NegotiatedCrypto { get; init; }

    /// <summary>
    /// Our own SDES crypto line generated for the answer (RFC 4568 §6.1). This carries a fresh,
    /// locally generated master key used to protect the RTP we send; it is the exact line placed
    /// in the outgoing SDP. Never a reflection of <see cref="NegotiatedCrypto"/>.
    /// <see langword="null"/> when SDES was not negotiated or the offered suite is unsupported.
    /// </summary>
    public SdpCryptoAttribute? LocalCrypto { get; init; }
}
