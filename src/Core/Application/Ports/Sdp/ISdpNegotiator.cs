using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Ports.Sdp;

/// <summary>
/// Port: SDP offer/answer negotiation.
/// Infrastructure/Sdp implements this; Infrastructure/Sip consumes it.
/// </summary>
public interface ISdpNegotiator
{
    /// <summary>Builds a default SDP offer or hold re-INVITE body.</summary>
    string BuildDefaultSdp(
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? options = null);

    /// <summary>
    /// Negotiates an SDP answer against a remote offer.
    /// Returns null when negotiation fails.
    /// </summary>
    string? TryBuildNegotiatedAnswer(
        string remoteOffer,
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? localOptions = null);

    /// <summary>
    /// Parses a remote SDP and extracts RTP session parameters.
    /// Returns null when the SDP cannot be parsed or has no usable audio stream.
    /// </summary>
    CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint);

    /// <summary>
    /// Parses a remote SDP and extracts RTP session parameters, honoring local negotiation
    /// options (e.g. <see cref="SdpMediaNegotiationOptions.PreferredCodecNames"/> for primary
    /// codec selection). The default implementation ignores the options so existing
    /// implementations stay source- and binary-compatible.
    /// </summary>
    CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        SdpMediaNegotiationOptions? localOptions)
        => TryParseMediaParameters(remoteSdp, localEndPoint);

    /// <summary>Returns true when the SDP signals remote hold semantics.</summary>
    bool IsRemoteHoldSdp(string? sdp);
}
