using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Builds the <see cref="IceMediaConsentSession"/> for a media leg from its negotiated ICE
/// parameters. Returns <see langword="null"/> when ICE, or the remote credentials needed to send a
/// consent check, are absent — so a non-ICE call runs no consent loop.
/// </summary>
internal static class IceMediaConsentSessionFactory
{
    // A valid PRIORITY for the consent check (RFC 8445 §5.1.2.1 host candidate: type pref 126,
    // local pref 65535, component 1). The media session has no candidate context; the value only
    // needs to be a well-formed PRIORITY the peer answers past — it does not affect response matching.
    private const uint DefaultConsentPriority = (126u << 24) | (65535u << 8) | 255u;

    /// <summary>
    /// Creates the consent session, or <see langword="null"/> when consent checks cannot run for the
    /// given parameters.
    /// </summary>
    /// <param name="parameters">The ICE view of the media 5-tuple (post ICE selection).</param>
    /// <param name="sendRaw">The media socket's raw-send delegate.</param>
    /// <param name="onConsentLost">Invoked once when consent expires.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public static IceMediaConsentSession? TryCreate(
        IceMediaParameters parameters,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        Action onConsentLost,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(onConsentLost);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!parameters.IceEnabled
            || string.IsNullOrWhiteSpace(parameters.LocalIceUfrag)
            || string.IsNullOrWhiteSpace(parameters.LocalIcePwd)
            || string.IsNullOrWhiteSpace(parameters.RemoteIceUfrag)
            || string.IsNullOrWhiteSpace(parameters.RemoteIcePwd))
        {
            return null;
        }

        return new IceMediaConsentSession(
            new StunMessageCodec(),
            sendRaw,
            parameters.RemoteEndPoint,
            parameters.LocalIceUfrag,
            parameters.RemoteIceUfrag,
            parameters.RemoteIcePwd,
            DefaultConsentPriority,
            parameters.IceControlling,
            IceTieBreaker.Derive(parameters.LocalIcePwd),
            onConsentLost,
            loggerFactory);
    }
}
