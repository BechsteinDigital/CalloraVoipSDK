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
        ArgumentNullException.ThrowIfNull(parameters);
        return TryCreate(parameters.SrtpSuite, parameters.SrtpLocalKeyParams, parameters.SrtpRemoteKeyParams, logger);
    }

    /// <summary>
    /// Builds the SRTP and SRTCP context pairs from raw negotiated SDES key material
    /// (RFC 4568/3711): outbound protects with our own key, inbound unprotects with the
    /// peer's key. SRTP and SRTCP derive independent session keys from the same master key
    /// (RFC 3711 §4.3.2). Returns all-<c>null</c> when no SDES material was negotiated (plain
    /// RTP or DTLS keying). Set-but-unparsable key material throws — failing open would silently
    /// send plaintext. This per-m-line overload lets each stream (audio, video) key from its own
    /// <c>a=crypto</c> attribute.
    /// </summary>
    public static (ISrtpContext? OutRtp, ISrtpContext? InRtp, ISrtcpContext? OutRtcp, ISrtcpContext? InRtcp)
        TryCreate(string? srtpSuite, string? localKeyParams, string? remoteKeyParams, ILogger logger)
    {
        if (TryParseKeyMaterial(srtpSuite, localKeyParams, remoteKeyParams) is not { } keys)
            return (null, null, null, null);

        logger.LogInformation(
            "SRTP and SRTCP enabled for media session (suite {Suite}).", srtpSuite);
        return (new SrtpContext(keys.Local), new SrtpContext(keys.Remote),
                new SrtcpContext(keys.Local), new SrtcpContext(keys.Remote));
    }

    /// <summary>
    /// Builds a second, independent SRTP context pair from the same SDES key material for a
    /// repair (RTX) stream (RFC 4588 §9): a separate context so the repair stream keeps its own
    /// rollover counter and replay window, distinct from the primary. RTP-only — the repair
    /// stream carries no RTCP. Returns <c>(null, null)</c> when no SDES material was negotiated;
    /// set-but-unparsable material throws (fail closed).
    /// </summary>
    public static (ISrtpContext? Outbound, ISrtpContext? Inbound) TryCreateSecondarySrtp(
        string? srtpSuite, string? localKeyParams, string? remoteKeyParams, ILogger logger)
    {
        if (TryParseKeyMaterial(srtpSuite, localKeyParams, remoteKeyParams) is not { } keys)
            return (null, null);

        logger.LogInformation("SRTP enabled for the RTX repair stream (suite {Suite}).", srtpSuite);
        return (new SrtpContext(keys.Local), new SrtpContext(keys.Remote));
    }

    /// <summary>
    /// Parses the local/remote SDES inline key material for a stream, or <see langword="null"/>
    /// when none was negotiated. A set-but-unparsable suite or key throws — failing open would
    /// silently send plaintext.
    /// </summary>
    private static (SrtpKeyMaterial Local, SrtpKeyMaterial Remote)? TryParseKeyMaterial(
        string? srtpSuite, string? localKeyParams, string? remoteKeyParams)
    {
        if (srtpSuite is null || localKeyParams is null || remoteKeyParams is null)
            return null;

        var suite = SrtpCryptoSuiteNames.TryParse(srtpSuite)
            ?? throw new InvalidOperationException(
                $"Negotiated SRTP suite '{srtpSuite}' is not supported by the media layer.");

        try
        {
            return (SrtpKeyMaterial.ParseInline(localKeyParams, suite),
                    SrtpKeyMaterial.ParseInline(remoteKeyParams, suite));
        }
        catch (FormatException ex)
        {
            // Fail closed: an SRTP-negotiated call must never fall back to plaintext.
            throw new InvalidOperationException(
                "Negotiated SRTP key material is invalid; refusing to start unencrypted media.", ex);
        }
    }
}
