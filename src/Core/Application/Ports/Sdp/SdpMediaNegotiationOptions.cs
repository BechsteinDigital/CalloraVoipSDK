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
    /// Ordered audio codec preference by SDP encoding name (e.g. "PCMU", "PCMA", "G722").
    /// When set, local offers and answers only include the listed codecs (plus DTMF
    /// telephone-event) in this order, and the primary codec for RTP sessions is chosen
    /// by this preference. Names not supported by the SDK are ignored; when nothing
    /// matches, the SDK default codec set is used. <see langword="null"/> keeps defaults.
    /// </summary>
    public IReadOnlyList<string>? PreferredCodecNames { get; init; }
}
