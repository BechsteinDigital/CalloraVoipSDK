using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Infrastructure implementation of <see cref="ICallMediaSessionFactory"/>.
/// Creates <see cref="RtpCallMediaSession"/> instances from negotiated SDP parameters.
/// Registered in the SDK facade (<see cref="Sdk.VoipClient"/>) to satisfy the Application port.
/// </summary>
/// <remarks>
/// When SDES key material was negotiated (<see cref="CallMediaParameters.SrtpKeys"/> is set),
/// the factory builds four crypto contexts (RFC 3711): SRTP contexts to protect outbound and
/// unprotect inbound RTP, plus SRTCP contexts (§3.4) to protect outbound and unprotect inbound
/// RTCP. The SRTP and SRTCP contexts share the same master keys but derive independent session
/// keys. If those keys cannot be turned into working contexts the factory throws instead of
/// silently sending cleartext, preventing an SRTP/SRTCP downgrade.
/// </remarks>
internal sealed class RtpCallMediaSessionFactory : ICallMediaSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RtpCallMediaSessionFactory> _logger;

    internal RtpCallMediaSessionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RtpCallMediaSessionFactory>();
    }

    /// <inheritdoc />
    public ICallMediaSession Create(CallMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.SrtpKeys is null)
            return new RtpCallMediaSession(parameters, _loggerFactory);

        var contexts = CreateSecureContexts(parameters.SrtpKeys);
        return new RtpCallMediaSession(
            parameters,
            _loggerFactory,
            contexts.OutboundSrtp,
            contexts.InboundSrtp,
            contexts.OutboundSrtcp,
            contexts.InboundSrtcp);
    }

    /// <summary>
    /// Builds the outbound (protect) and inbound (unprotect) SRTP and SRTCP contexts from
    /// negotiated SDES key material. The local master key/salt drives the outbound direction,
    /// the remote master key/salt the inbound direction. Throws when the suite or key lengths
    /// cannot produce valid contexts, so a negotiated-but-unbuildable secure session never
    /// degrades to cleartext RTP/RTCP.
    /// </summary>
    private (ISrtpContext OutboundSrtp, ISrtpContext InboundSrtp,
             ISrtcpContext OutboundSrtcp, ISrtcpContext InboundSrtcp)
        CreateSecureContexts(SrtpSessionKeyMaterial keys)
    {
        SrtpCryptoSuite suite;
        try
        {
            suite = SrtpCryptoSuiteMapper.FromDomainKind(keys.Suite);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw CreateDowngradeException($"the negotiated crypto suite '{keys.Suite}' is not supported", ex);
        }

        ValidateKeyMaterial(suite, keys);

        var localMaterial  = ToKeyMaterial(keys.LocalMasterKey, keys.LocalMasterSalt, suite);
        var remoteMaterial = ToKeyMaterial(keys.RemoteMasterKey, keys.RemoteMasterSalt, suite);

        ISrtpContext? outboundSrtp = null;
        ISrtpContext? inboundSrtp = null;
        ISrtcpContext? outboundSrtcp = null;
        try
        {
            outboundSrtp  = new SrtpContext(localMaterial);
            inboundSrtp   = new SrtpContext(remoteMaterial);
            outboundSrtcp = new SrtcpContext(localMaterial);
            var inboundSrtcp = new SrtcpContext(remoteMaterial);
            return (outboundSrtp, inboundSrtp, outboundSrtcp, inboundSrtcp);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            (outboundSrtp as IDisposable)?.Dispose();
            (inboundSrtp as IDisposable)?.Dispose();
            (outboundSrtcp as IDisposable)?.Dispose();
            throw CreateDowngradeException(
                "the negotiated key material could not initialize an SRTP/SRTCP context", ex);
        }
    }

    /// <summary>
    /// Verifies the negotiated key/salt lengths against the suite before context creation.
    /// A mismatch means the negotiation cannot be honored, so we refuse rather than downgrade.
    /// </summary>
    private void ValidateKeyMaterial(SrtpCryptoSuite suite, SrtpSessionKeyMaterial keys)
    {
        var expectedKeyLength = SrtpCryptoSuiteMapper.GetMasterKeyLength(suite);
        const int expectedSaltLength = SrtpCryptoSuiteMapper.MasterSaltLength;

        RequireLength("local master key", keys.LocalMasterKey.Length, expectedKeyLength, suite);
        RequireLength("local master salt", keys.LocalMasterSalt.Length, expectedSaltLength, suite);
        RequireLength("remote master key", keys.RemoteMasterKey.Length, expectedKeyLength, suite);
        RequireLength("remote master salt", keys.RemoteMasterSalt.Length, expectedSaltLength, suite);
    }

    private void RequireLength(string name, int actualLength, int expectedLength, SrtpCryptoSuite suite)
    {
        if (actualLength == expectedLength)
            return;

        throw CreateDowngradeException(
            $"the {name} is {actualLength} bytes but suite {suite} requires {expectedLength} bytes",
            inner: null);
    }

    private static SrtpKeyMaterial ToKeyMaterial(
        ReadOnlyMemory<byte> masterKey,
        ReadOnlyMemory<byte> masterSalt,
        SrtpCryptoSuite suite)
        => new()
        {
            MasterKey  = masterKey,
            MasterSalt = masterSalt,
            Suite      = suite,
        };

    private InvalidOperationException CreateDowngradeException(string detail, Exception? inner)
    {
        _logger.LogError(inner, "Refusing to create RTP media session: {Detail}.", detail);
        return new InvalidOperationException(
            $"SRTP was negotiated for this call but {detail}. "
            + "Refusing to fall back to cleartext RTP (SRTP downgrade protection).",
            inner);
    }
}
