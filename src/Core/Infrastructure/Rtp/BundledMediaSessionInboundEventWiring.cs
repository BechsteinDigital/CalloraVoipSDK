using System;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Wires a <see cref="BundledMediaSession"/>'s inbound-media event fan-out — the per-video-track frame/key-frame
/// subscriptions and the guarded additional-audio raise — into a collaborator, so the session file stays under the
/// size limit (R3) while keeping the raise semantics byte-identical (mirroring the existing collaborator split:
/// <see cref="BundledMediaSessionTrackMutation"/>, <see cref="BundledMediaSessionComposition"/>). The session's
/// inbound events can only be invoked from within the declaring type, so they stay on the session; this collaborator
/// receives thin raise delegates (each just null-conditionally invokes the corresponding session event) and owns the
/// subscription plumbing that both the construction-time loop and the live <c>AddVideoTrack</c> path use.
/// </summary>
internal sealed class BundledMediaSessionInboundEventWiring
{
    private readonly Action<InboundVideoFrame> _raiseVideoFrame;
    private readonly Action<string, InboundVideoFrame> _raiseVideoTrack;
    private readonly Action<string, string, InboundVideoFrame> _raiseVideoLayer;
    private readonly Action _raiseKeyFrameRequested;
    private readonly Action<string, RtpPacket> _raiseAudioTrack;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the wiring collaborator over the session's raise delegates. Each delegate null-conditionally invokes
    /// the corresponding <see cref="BundledMediaSession"/> event, so wiring done here is indistinguishable from the
    /// former in-session wiring.
    /// </summary>
    /// <param name="raiseVideoFrame">Raises the mid-less primary <c>VideoFrameReceived</c> event.</param>
    /// <param name="raiseVideoTrack">Raises the mid-tagged <c>VideoTrackFrameReceived</c> event.</param>
    /// <param name="raiseVideoLayer">Raises the per-layer <c>VideoLayerFrameReceived</c> event (mid, rid).</param>
    /// <param name="raiseKeyFrameRequested">Raises the <c>VideoKeyFrameRequested</c> event.</param>
    /// <param name="raiseAudioTrack">Raises the mid-tagged <c>AudioTrackFrameReceived</c> event.</param>
    /// <param name="logger">Logs (and suppresses) a throwing additional-audio subscriber so the shared receive loop survives (K3).</param>
    public BundledMediaSessionInboundEventWiring(
        Action<InboundVideoFrame> raiseVideoFrame,
        Action<string, InboundVideoFrame> raiseVideoTrack,
        Action<string, string, InboundVideoFrame> raiseVideoLayer,
        Action raiseKeyFrameRequested,
        Action<string, RtpPacket> raiseAudioTrack,
        ILogger logger)
    {
        _raiseVideoFrame = raiseVideoFrame ?? throw new ArgumentNullException(nameof(raiseVideoFrame));
        _raiseVideoTrack = raiseVideoTrack ?? throw new ArgumentNullException(nameof(raiseVideoTrack));
        _raiseVideoLayer = raiseVideoLayer ?? throw new ArgumentNullException(nameof(raiseVideoLayer));
        _raiseKeyFrameRequested = raiseKeyFrameRequested ?? throw new ArgumentNullException(nameof(raiseKeyFrameRequested));
        _raiseAudioTrack = raiseAudioTrack ?? throw new ArgumentNullException(nameof(raiseAudioTrack));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Wires one video track's inbound events onto the session surface. Exactly ONE surface fires per frame so a
    /// frame is never delivered twice: a RID-tagged frame is a demultiplexed simulcast layer (RFC 8853) and fires
    /// ONLY the per-layer <c>VideoLayerFrameReceived</c>; a RID-less frame (primary/default stream) fires the
    /// mid-less <c>VideoFrameReceived</c> (primary track only) and the mid-tagged <c>VideoTrackFrameReceived</c>,
    /// exactly as before — so the non-simulcast path stays byte-identical. Used by both the ctor loop and the live
    /// <see cref="BundledMediaSessionTrackMutation.AddVideoTrack"/>; a live-added track is never the primary.
    /// </summary>
    /// <param name="mid">The MID the frames are tagged with on the mid-carrying surfaces.</param>
    /// <param name="track">The video track whose inbound frame / key-frame events are subscribed.</param>
    /// <param name="isPrimary">Whether this is the primary (ctor-first) track — only it drives the mid-less facade.</param>
    public void WireVideoTrackEvents(string mid, BundledVideoTrack track, bool isPrimary)
    {
        track.FrameReceived += (frame, rid) =>
        {
            if (rid is not null)
            {
                // A demultiplexed simulcast layer (RFC 8853): surface it ONLY on the per-layer event, so a layer
                // frame is delivered exactly once and never also on the RID-less surfaces.
                _raiseVideoLayer(mid, rid, frame);
                return;
            }

            if (isPrimary)
                _raiseVideoFrame(frame);
            _raiseVideoTrack(mid, frame);
        };
        track.KeyFrameRequested += () => _raiseKeyFrameRequested();
    }

    /// <summary>
    /// Raises the mid-tagged inbound-audio event for an additional audio track, guarding it so a throwing
    /// subscriber never tears down the shared receive loop (K3). Used for both the ctor-time additional tracks and
    /// the live-added ones so the guard semantics are identical on both paths.
    /// </summary>
    /// <param name="mid">The additional audio track's MID.</param>
    /// <param name="packet">The decrypted inbound RTP packet for that MID.</param>
    public void RaiseAudioTrackReceivedGuarded(string mid, RtpPacket packet)
    {
        try
        {
            _raiseAudioTrack(mid, packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled audio AudioTrackFrameReceived handler for MID '{Mid}'.", mid);
        }
    }
}
