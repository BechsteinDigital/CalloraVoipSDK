using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Builds the SRTP/SRTCP context pairs for a call leg keyed via SDES (RFC 4568).
/// Extracted from <see cref="RtpCallMediaSession"/> so the session stays within size
/// limits now that DTLS-SRTP keying (contexts created post-handshake by
/// <see cref="Dtls.DtlsMediaAttachment"/>) exists alongside it.
/// </summary>
internal static class SdesMediaCryptoContextFactory
{
    /// <summary>
    /// Builds the SRTP and SRTCP context pairs from negotiated SDES key material
    /// (RFC 4568/3711): outbound protects with our own key, inbound unprotects with the
    /// peer's key. SRTP and SRTCP derive independent session keys from the same master key
    /// (RFC 3711 §4.3.2). Returns all-<c>null</c> when the call is plain RTP or keyed via
    /// DTLS instead. A negotiated-SRTP call with unparsable key material throws — failing
    /// open would silently send plaintext.
    /// </summary>
    public static (ISrtpContext? OutRtp, ISrtpContext? InRtp, ISrtcpContext? OutRtcp, ISrtcpContext? InRtcp)
        TryCreate(CallMediaParameters parameters, ILogger logger)
    {
        if (parameters.SrtpSuite is null
            || parameters.SrtpLocalKeyParams is null
            || parameters.SrtpRemoteKeyParams is null)
        {
            return (null, null, null, null);
        }

        var suite = SrtpCryptoSuiteNames.TryParse(parameters.SrtpSuite)
            ?? throw new InvalidOperationException(
                $"Negotiated SRTP suite '{parameters.SrtpSuite}' is not supported by the media layer.");

        SrtpKeyMaterial localKeys;
        SrtpKeyMaterial remoteKeys;
        try
        {
            localKeys = SrtpKeyMaterial.ParseInline(parameters.SrtpLocalKeyParams, suite);
            remoteKeys = SrtpKeyMaterial.ParseInline(parameters.SrtpRemoteKeyParams, suite);
        }
        catch (FormatException ex)
        {
            // Fail closed: an SRTP-negotiated call must never fall back to plaintext.
            throw new InvalidOperationException(
                "Negotiated SRTP key material is invalid; refusing to start unencrypted media.", ex);
        }

        logger.LogInformation(
            "SRTP and SRTCP enabled for media session (suite {Suite}).",
            parameters.SrtpSuite);
        return (new SrtpContext(localKeys), new SrtpContext(remoteKeys),
                new SrtcpContext(localKeys), new SrtcpContext(remoteKeys));
    }
}
