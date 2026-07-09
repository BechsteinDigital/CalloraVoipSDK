using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Infrastructure implementation of <see cref="ICallMediaSessionFactory"/>.
/// Creates <see cref="RtpCallMediaSession"/> instances from negotiated SDP parameters.
/// Registered in the SDK facade (<see cref="Sdk.VoipClient"/>) to satisfy the Application port.
/// </summary>
internal sealed class RtpCallMediaSessionFactory : ICallMediaSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly PayloadCodecKind? _bridgeTapCodec;

    /// <param name="loggerFactory">Logger factory for created sessions.</param>
    /// <param name="bridgeTapCodec">
    /// Fixed codec the media consumer (bridge/tap) expects, transcoded from/to the negotiated
    /// wire codec. <see langword="null"/> delivers the raw wire payload (passthrough).
    /// </param>
    internal RtpCallMediaSessionFactory(ILoggerFactory loggerFactory, PayloadCodecKind? bridgeTapCodec = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _bridgeTapCodec = bridgeTapCodec;
    }

    /// <inheritdoc />
    public ICallMediaSession Create(CallMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new RtpCallMediaSession(parameters, _loggerFactory, _bridgeTapCodec);
    }
}
