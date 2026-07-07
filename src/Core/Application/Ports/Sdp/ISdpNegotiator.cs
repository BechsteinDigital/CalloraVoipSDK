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
    /// <param name="remoteSdp">The far end's SDP (source of remote SDES key material).</param>
    /// <param name="localEndPoint">Local UDP endpoint to bind RTP to.</param>
    /// <param name="localSdp">
    /// Optional SDP we advertised (our answer/offer). When it carries a matching SDES
    /// <c>a=crypto</c> line, both keys are composed into <c>CallMediaParameters.SrtpKeys</c>.
    /// </param>
    CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        string? localSdp = null);

    /// <summary>Returns true when the SDP signals remote hold semantics.</summary>
    bool IsRemoteHoldSdp(string? sdp);
}
