using System;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Serialises gathered local ICE candidates to their RFC 8829 <c>candidate:</c> line and dispatches each
/// to the peer's out-of-band trickle sink (RFC 8838), isolating an app-supplied handler from the media
/// core: a throwing handler is logged and swallowed so a bad consumer callback never faults gathering.
/// Extracted from <see cref="WebRtcPeerConnection"/> to keep that file within the size limit, mirroring the
/// existing collaborator split (parsing, gathering, SDP options).
/// </summary>
internal sealed class WebRtcLocalCandidateEmitter
{
    private readonly Func<Action<string>?> _handlerSnapshot;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the emitter over a snapshot provider for the peer's trickle handler. The provider is read on
    /// each emission so a handler subscribed after construction is honoured and the current subscriber is
    /// captured atomically (the peer's public event may be reassigned between emissions).
    /// </summary>
    /// <param name="handlerSnapshot">Returns the current trickle handler, or <see langword="null"/> when none is subscribed.</param>
    /// <param name="logger">Logs a handler fault without propagating it into the gathering path.</param>
    public WebRtcLocalCandidateEmitter(Func<Action<string>?> handlerSnapshot, ILogger logger)
    {
        _handlerSnapshot = handlerSnapshot ?? throw new ArgumentNullException(nameof(handlerSnapshot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Emits the local host candidate for <paramref name="local"/> (the early-bound media endpoint) as an
    /// RFC 8829 candidate line on the trickle sink.
    /// </summary>
    public void EmitLocalHost(IPEndPoint local) => Emit(WebRtcIceCandidateFactory.LocalHostCandidate(local));

    /// <summary>
    /// Emits a gathered candidate as an RFC 8829 <c>candidate:</c> line on the trickle sink. A no-op when no
    /// handler is subscribed. A handler that throws is logged and swallowed — it never faults the caller.
    /// </summary>
    public void Emit(SdpIceCandidate candidate)
    {
        if (_handlerSnapshot() is not { } handler)
            return;

        var line = "candidate:" + candidate.Serialize();
        try
        {
            handler(line);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in WebRTC LocalIceCandidateDiscovered handler.");
        }
    }
}
