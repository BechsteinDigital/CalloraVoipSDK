using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;

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
    private readonly IDtlsSrtpHandshaker? _dtlsHandshaker;
    private readonly DtlsCertificate? _dtlsCertificate;

    /// <param name="loggerFactory">Logger factory for created sessions.</param>
    /// <param name="bridgeTapCodec">
    /// Fixed codec the media consumer (bridge/tap) expects, transcoded from/to the negotiated
    /// wire codec. <see langword="null"/> delivers the raw wire payload (passthrough).
    /// </param>
    /// <param name="dtlsHandshaker">
    /// DTLS-SRTP handshake engine for DTLS-keyed call legs (RFC 5763). <see langword="null"/>
    /// makes a DTLS-negotiated leg fail closed at session creation.
    /// </param>
    /// <param name="dtlsCertificate">
    /// Local DTLS identity whose fingerprint is signaled in SDP. <see langword="null"/>
    /// makes a DTLS-negotiated leg fail closed at session creation.
    /// </param>
    internal RtpCallMediaSessionFactory(
        ILoggerFactory loggerFactory,
        PayloadCodecKind? bridgeTapCodec = null,
        IDtlsSrtpHandshaker? dtlsHandshaker = null,
        DtlsCertificate? dtlsCertificate = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _bridgeTapCodec = bridgeTapCodec;
        _dtlsHandshaker = dtlsHandshaker;
        _dtlsCertificate = dtlsCertificate;
    }

    /// <inheritdoc />
    public ICallMediaSession Create(CallMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new RtpCallMediaSession(
            parameters, _loggerFactory, _bridgeTapCodec, _dtlsHandshaker, _dtlsCertificate);
    }
}
