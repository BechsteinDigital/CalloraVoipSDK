using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Sdp;

/// <summary>
/// Default ISdpNegotiator implementation delegating to SdpUtilities.
/// </summary>
internal sealed class SdpNegotiator : ISdpNegotiator
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the negotiator. The optional logger is forwarded to the SDP helpers so that
    /// unparseable remote SDP is observable rather than silently discarded (HARD-G3).
    /// </summary>
    public SdpNegotiator(ILogger<SdpNegotiator>? logger = null) => _logger = logger;

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
        => SdpUtilities.TryBuildNegotiatedAnswer(remoteOffer, localEndPoint, hold, localOptions, _logger);

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(string remoteSdp, IPEndPoint localEndPoint)
        => SdpUtilities.TryParseMediaParameters(remoteSdp, localEndPoint, logger: _logger);

    /// <inheritdoc />
    public CallMediaParameters? TryParseMediaParameters(
        string remoteSdp,
        IPEndPoint localEndPoint,
        SdpMediaNegotiationOptions? localOptions)
        => SdpUtilities.TryParseMediaParameters(remoteSdp, localEndPoint, localOptions, _logger);

    /// <inheritdoc />
    public bool IsRemoteHoldSdp(string? sdp)
        => SdpUtilities.IsRemoteHoldSdp(sdp, _logger);
}
