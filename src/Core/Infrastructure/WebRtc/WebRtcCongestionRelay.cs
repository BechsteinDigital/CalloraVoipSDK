using System;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Projects the built <see cref="BundledMediaSession"/>'s sender-side transport-wide congestion signal
/// (transport-cc) onto the peer's public WebRTC surface, factored out of
/// <see cref="WebRtcPeerConnection"/> so that file stays within the size limit (mirroring the existing
/// collaborator split — gathering, SDP options, candidate emission, the session-event bridge). Two halves:
/// <list type="bullet">
/// <item><description>the reactive half — <see cref="WireSession"/> subscribes the session's
/// <c>RecommendedBitrateChanged</c> and fans each revision (bitrate + coarse quality) out through the peer's
/// raise delegate;</description></item>
/// <item><description>the point-in-time half — <see cref="RecommendedOutgoingBitrateBps"/> and
/// <see cref="OutgoingNetworkQuality"/> read straight through a caller-supplied snapshot of the live session,
/// so they reflect the current recommendation (or <see langword="null"/> when no session is built or
/// transport-cc was not negotiated).</description></item>
/// </list>
/// Pure event/projection wiring: it holds no congestion state of its own and takes no lock. The peer owns the
/// <c>_sync</c>-guarded session field and passes a snapshot delegate in; this relay only reads through it.
/// </summary>
internal sealed class WebRtcCongestionRelay
{
    private readonly Func<BundledMediaSession?> _session;

    /// <summary>
    /// Creates the relay over the peer's live-session snapshot. The delegate reads the peer's
    /// <c>_sync</c>-guarded session field under that lock, so the point-in-time projections stay consistent
    /// with the peer's session lifecycle.
    /// </summary>
    /// <param name="session">Snapshots the peer's current media session, or <see langword="null"/> before one is built.</param>
    public WebRtcCongestionRelay(Func<BundledMediaSession?> session)
        => _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// The sender-side congestion controller's current recommended outbound bitrate in bits/second, or
    /// <see langword="null"/> when no session is built or transport-cc was not negotiated for this leg.
    /// </summary>
    public long? RecommendedOutgoingBitrateBps => _session()?.Congestion?.RecommendedBitrateBps;

    /// <summary>
    /// The sender-side congestion controller's current coarse network quality, or <see langword="null"/> when
    /// no session is built or transport-cc was not negotiated for this leg.
    /// </summary>
    public NetworkQuality? OutgoingNetworkQuality => _session()?.Congestion?.Quality;

    /// <summary>
    /// Subscribes the freshly built session's congestion controller and fans each recommended-bitrate revision
    /// out through the peer's raise delegate as one surface (bitrate + the controller's current coarse quality).
    /// Runs once, right after the session is built; it registers a handler only and never reads peer state. When
    /// transport-cc was not negotiated the controller is null, so <paramref name="raiseRecommendedBitrateChanged"/>
    /// stays silent.
    /// </summary>
    /// <param name="session">The freshly built media session whose congestion controller is wired.</param>
    /// <param name="raiseRecommendedBitrateChanged">Raises the peer's recommended-bitrate event (bitrate, quality).</param>
    public void WireSession(
        BundledMediaSession session,
        Action<long, NetworkQuality> raiseRecommendedBitrateChanged)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(raiseRecommendedBitrateChanged);
        // The controller's event carries the bitrate only; pair it with the current quality read at raise time
        // (both come from the same feedback fold, so the quality reflects the same report as the new bitrate).
        if (session.Congestion is { } controller)
            controller.RecommendedBitrateChanged += bps => raiseRecommendedBitrateChanged(bps, controller.Quality);
    }
}
