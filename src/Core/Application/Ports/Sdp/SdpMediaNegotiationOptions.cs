using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Ports.Sdp;

/// <summary>
/// ICE parameters for local SDP offer/answer generation.
/// </summary>
public sealed class SdpIceNegotiationOptions
{
    /// <summary>
    /// Local ICE username fragment.
    /// </summary>
    public required string Ufrag { get; init; }

    /// <summary>
    /// Local ICE password.
    /// </summary>
    public required string Pwd { get; init; }

    /// <summary>
    /// Local ICE candidates emitted into SDP.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Optional ICE options value (for example trickle).
    /// </summary>
    public string? Options { get; init; }
}

/// <summary>
/// Optional runtime parameters that influence SDP negotiation output.
/// </summary>
public sealed class SdpMediaNegotiationOptions
{
    /// <summary>
    /// ICE settings to include in local SDP.
    /// </summary>
    public SdpIceNegotiationOptions? Ice { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a locally built offer advertises SDES SRTP (RFC 4568):
    /// one <c>a=crypto</c> line with freshly generated key material plus the <c>RTP/SAVP</c>
    /// profile. Ignored on the answer path, which keys via the offered crypto.
    /// <see langword="false"/> keeps a plain <c>RTP/AVP</c> offer.
    /// </summary>
    public bool OfferSrtpCrypto { get; init; }

    /// <summary>
    /// Ordered audio codec preference by SDP encoding name (e.g. "PCMU", "PCMA", "G722").
    /// When set, local offers and answers only include the listed codecs (plus DTMF
    /// telephone-event) in this order, and the primary codec for RTP sessions is chosen
    /// by this preference. Names not supported by the SDK are ignored; when nothing
    /// matches, the SDK default codec set is used. <see langword="null"/> keeps defaults.
    /// </summary>
    public IReadOnlyList<string>? PreferredCodecNames { get; init; }

    /// <summary>
    /// Origin session id (<c>o=</c> sess-id, RFC 4566 §5.2) for locally built SDP. Stable
    /// across a call leg. <c>0</c> keeps the legacy constant.
    /// </summary>
    public long SessionId { get; init; }

    /// <summary>
    /// Origin session version (<c>o=</c> sess-version, RFC 4566 §5.2) for locally built SDP.
    /// The caller increments it on every media change (offer/answer/hold/unhold/re-INVITE) so
    /// the peer detects the modification (RFC 3264 §5). <c>0</c> keeps the legacy constant.
    /// </summary>
    public long SessionVersion { get; init; }
}
