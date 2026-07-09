using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Builds the <see cref="IceInboundStunHandler"/> for a media leg from its negotiated ICE
/// short-term credentials, wiring it to the media socket's raw-send delegate. Returns
/// <see langword="null"/> when no local ICE credentials are present, so a non-ICE call adds no
/// handler and stays on the plain RTP path.
/// </summary>
internal static class IceInboundStunHandlerFactory
{
    /// <summary>
    /// Creates the inbound STUN handler, or <see langword="null"/> when
    /// <paramref name="localUfrag"/> / <paramref name="localPassword"/> are absent.
    /// </summary>
    /// <param name="localUfrag">Local ICE username fragment from the negotiated media parameters.</param>
    /// <param name="localPassword">Local ICE password from the negotiated media parameters.</param>
    /// <param name="sendRaw">The media socket's raw-send delegate (e.g. <c>RtpSession.SendRawAsync</c>).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public static IceInboundStunHandler? Create(
        string? localUfrag,
        string? localPassword,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (string.IsNullOrWhiteSpace(localUfrag) || string.IsNullOrWhiteSpace(localPassword))
            return null;

        var processor = new IceInboundCheckProcessor(new IceInboundBindingResponder(new StunMessageCodec()));

        // The handler starts controlling with a fresh tie-breaker. Sharing role and tie-breaker
        // with the outbound CallIceAgent — so a role conflict resolves identically in both
        // directions — is a follow-up; the common offerer=controlling / answerer=controlled case
        // has no conflict, and inbound checks are still authenticated and answered correctly.
        return new IceInboundStunHandler(
            processor,
            sendRaw,
            localUfrag,
            localPassword,
            IceTieBreaker.Generate(),
            IceRole.Controlling,
            loggerFactory);
    }
}
