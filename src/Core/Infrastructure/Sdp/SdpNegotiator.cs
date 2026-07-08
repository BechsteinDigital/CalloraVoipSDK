using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Default ISdpNegotiator implementation delegating to SdpUtilities.
/// </summary>
internal sealed class SdpNegotiator : ISdpNegotiator
{
    /// <inheritdoc />
    public string BuildDefaultSdp(
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? options = null)
        => SdpUtilities.BuildDefaultSdp(localEndPoint, hold, options);

    /// <inheritdoc />
    public string? TryBuildNegotiatedAnswer(
        string remoteOffer,
        IPEndPoint localEndPoint,
        bool hold,
        SdpMediaNegotiationOptions? localOptions = null)
        => SdpUtilities.TryBuildNegotiatedAnswer(remoteOffer, localEndPoint, hold, localOptions);

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint)
        => SdpUtilities.TryParseMediaParameters(remoteSdp, localEndPoint);

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        SdpMediaNegotiationOptions? localOptions)
        => SdpUtilities.TryParseMediaParameters(remoteSdp, localEndPoint, localOptions);

    /// <inheritdoc />
    public bool IsRemoteHoldSdp(string? sdp)
        => SdpUtilities.IsRemoteHoldSdp(sdp);
}
