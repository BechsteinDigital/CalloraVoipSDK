using System;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Bridges a built <see cref="BundledMediaSession"/>'s media-transport lifecycle and inbound-media events onto
/// the peer's public WebRTC surface, and fires the RFC 8829 connection- and signalling-state change events,
/// isolating this event fan-out from <see cref="WebRtcPeerConnection"/> to keep that file within the size limit
/// (mirroring the existing collaborator split — gathering, SDP options, candidate emission). Pure event wiring:
/// it holds no peer state and takes no lock; the peer keeps sole ownership of the <c>_sync</c>-guarded
/// connection/signalling state, which this bridge never reads or writes. The peer snapshots each event delegate
/// under its own lock and passes it in; the bridge only invokes it (outside the lock), preserving the
/// snapshot-inside-lock/invoke-outside-lock discipline (K3).
/// </summary>
internal sealed class WebRtcSessionEventBridge
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the bridge over the peer's logger, used to log (and swallow) a throwing state handler so it
    /// never breaks the signalling path (K3).
    /// </summary>
    /// <param name="logger">Logs a state-change handler fault without propagating it.</param>
    public WebRtcSessionEventBridge(ILogger logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Wires the session's transport-lifecycle and inbound-media events onto the peer's surface. Transport
    /// lifecycle maps onto the WebRTC connection state (RFC 8829) via <paramref name="transitionTo"/>: keys
    /// installed → Connected, handshake failure or consent loss → Failed, a transient consent miss →
    /// Disconnected. Inbound media is surfaced through the peer's raise delegates. Runs once, single-threaded,
    /// right after the session is built; it registers handlers only and never reads peer state.
    /// </summary>
    /// <param name="session">The freshly built media session whose events are wired.</param>
    /// <param name="transitionTo">The peer's connection-state transition (owns the <c>_sync</c>-guarded state).</param>
    /// <param name="raiseAudioReceived">Raises the peer's primary inbound-audio event.</param>
    /// <param name="raiseAudioTrackFrameReceived">Raises the peer's mid-tagged additional inbound-audio event (4.7.0).</param>
    /// <param name="raiseVideoFrameReceived">Raises the peer's inbound-video-frame event.</param>
    /// <param name="raiseVideoTrackFrameReceived">Raises the peer's mid-tagged inbound-video-frame event.</param>
    /// <param name="raiseVideoLayerFrameReceived">Raises the peer's per-layer (mid, rid) inbound-video-frame event for recv-side simulcast/SFU forwarding (4.7.0).</param>
    /// <param name="raiseVideoKeyFrameRequested">Raises the peer's inbound key-frame-request event.</param>
    /// <param name="raiseDtmfReceived">Raises the peer's inbound-DTMF event.</param>
    public void WireSession(
        BundledMediaSession session,
        Action<WebRtcConnectionState> transitionTo,
        Action<byte[]> raiseAudioReceived,
        Action<string, byte[]> raiseAudioTrackFrameReceived,
        Action<byte[], uint, bool> raiseVideoFrameReceived,
        Action<string, byte[], uint, bool> raiseVideoTrackFrameReceived,
        Action<string, string, byte[], uint, bool> raiseVideoLayerFrameReceived,
        Action raiseVideoKeyFrameRequested,
        Action<byte, int> raiseDtmfReceived)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(raiseAudioTrackFrameReceived);
        ArgumentNullException.ThrowIfNull(raiseVideoLayerFrameReceived);
        session.Connected += () => transitionTo(WebRtcConnectionState.Connected);
        session.HandshakeFailed += () => transitionTo(WebRtcConnectionState.Failed);
        session.MediaConsentLost += () => transitionTo(WebRtcConnectionState.Failed);
        session.MediaConnectivityDegraded += () => transitionTo(WebRtcConnectionState.Disconnected);
        session.MediaConnectivityRecovered += () => transitionTo(WebRtcConnectionState.Connected);
        session.AudioReceived += packet => raiseAudioReceived(packet.Payload.ToArray());
        // Mid-tagged inbound audio (4.7.0) → the receiver routes each frame to its remote audio track.
        session.AudioTrackFrameReceived += (mid, packet) => raiseAudioTrackFrameReceived(mid, packet.Payload.ToArray());
        session.VideoFrameReceived += (frame, timestamp, isKeyFrame) => raiseVideoFrameReceived(frame, timestamp, isKeyFrame);
        // Mid-tagged inbound video (P2b) → the receiver routes each frame to its remote track (P2c).
        session.VideoTrackFrameReceived += (mid, frame, timestamp, isKeyFrame) => raiseVideoTrackFrameReceived(mid, frame, timestamp, isKeyFrame);
        // Per-layer inbound video (4.7.0 recv-side simulcast, RFC 8853) → the receiver forwards each demuxed
        // encoding tagged with its a=rid; fires only for RID-tagged layers, never the primary RID-less stream.
        session.VideoLayerFrameReceived += (mid, rid, frame, timestamp, isKeyFrame) => raiseVideoLayerFrameReceived(mid, rid, frame, timestamp, isKeyFrame);
        session.VideoKeyFrameRequested += () => raiseVideoKeyFrameRequested();
        session.DtmfReceived += (toneCode, durationMs) => raiseDtmfReceived(toneCode, durationMs);
    }

    /// <summary>
    /// Fires the connection-state change (RFC 8829 <c>connectionstatechange</c>) for a transition already
    /// committed to the peer's connection state under its lock at the call site. Invokes the passed
    /// <paramref name="handler"/> (the delegate the peer snapshotted) outside the lock; a throwing handler is
    /// logged, not propagated. A null handler is a no-op.
    /// </summary>
    /// <param name="handler">The peer's connection-state-changed delegate snapshot, or null when none is subscribed.</param>
    /// <param name="next">The state just committed.</param>
    public void RaiseConnectionState(Action<WebRtcConnectionState>? handler, WebRtcConnectionState next)
    {
        if (handler is null)
            return;

        try
        {
            handler(next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in WebRTC ConnectionStateChanged handler.");
        }
    }

    /// <summary>
    /// Fires the signalling-state change (the W3C <c>signalingstatechange</c>) for a transition already
    /// committed to the peer's signalling state under its lock at the call site. Invokes the passed
    /// <paramref name="handler"/> (the delegate the peer snapshotted inside its lock) outside the lock (K3), so
    /// a handler may re-subscribe without deadlocking; a throwing handler is logged, not propagated (handlers
    /// must not break the signalling path). A null handler is a no-op.
    /// </summary>
    /// <param name="handler">The peer's signalling-state-changed delegate snapshot, or null when none is subscribed.</param>
    /// <param name="next">The signalling state just committed.</param>
    public void RaiseSignalingState(Action<WebRtcSignalingState>? handler, WebRtcSignalingState next)
    {
        if (handler is null)
            return;

        try
        {
            handler(next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in WebRTC SignalingStateChanged handler.");
        }
    }
}
