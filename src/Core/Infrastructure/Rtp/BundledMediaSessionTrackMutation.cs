using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The live (mid-call) track-mutation engine of one <see cref="BundledMediaSession"/> (4.7.0 renegotiation,
/// P3b-3): adds or deactivates a video or an additional audio track on a running bundle — while the receive loop
/// runs and media flows on the existing tracks — without touching the shared transport, DTLS association, ICE
/// agent, or SRTP context, and without interrupting any existing track (including the primary audio anchor).
/// Extracted from <see cref="BundledMediaSession"/> so that session stays a wiring/lifecycle unit under the
/// 1000-line rule; the behaviour is byte-identical to the former inline methods.
/// </summary>
/// <remarks>
/// Threading (K3): every mutation runs under the <em>same</em> gate object the session created and hands in here
/// — this collaborator does not introduce a second lock, it participates in the one that already serialises the
/// session's control-plane mutations against each other. The receive loop is NOT gated by it (it reads the router
/// and the track sets lock-free); the gate only orders add/remove against add/remove. The registration order is
/// deliberate and race-free against the single-consumer receive loop: the demux boundary is extended first (so an
/// inbound packet for the new MID is no longer rejected as unknown), then the outbound sender, then the inbound
/// sink last — during the window after the MID is known but before the sink exists a packet is cleanly
/// dropped/counted by the router, never mis-delivered. Inbound demultiplexes by the MID header extension
/// (RFC 9143). A deactivate mirrors the add in reverse; a deactivated VIDEO track is not disposed here (the receive
/// loop may be inside its <c>OnRtpPacket</c>) but appended to the session's deferred-dispose list for
/// <c>DisposeAsync</c> to drain (HARD-C6). Audio has no per-track object, so it needs no deferred dispose.
/// </remarks>
internal sealed class BundledMediaSessionTrackMutation
{
    private readonly object _gate;
    private readonly Func<bool> _isDisposed;
    private readonly BundledTrackRouter _router;
    private readonly BundledOutboundPipeline _outbound;
    private readonly BundledVideoTrackSet _video;
    private readonly BundledAudioTrackSet _audioTracks;
    private readonly BundledOutboundSsrcTracker _outboundSsrcs;
    private readonly List<BundledVideoTrack> _deactivatedVideoTracks;
    private readonly BundledMediaSessionOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _primaryAudioMid;
    private readonly Action<string, BundledVideoTrack, bool> _wireVideoTrackEvents;
    private readonly Action<string, RtpPacket> _raiseAudioTrackReceivedGuarded;

    // Which stream every SSRC belongs to. Both of its maps used to be construction-time snapshots (#161
    // P2-11), so a track added mid-call was invisible to the metrics; a mutation now feeds them.
    private readonly BundledStreamAttribution _attribution;

    /// <summary>
    /// Creates the mutation engine over the session's collaborators. The <paramref name="gate"/> MUST be the same
    /// object the session locks for its own dispose/mutation ordering (this is a shared lock, not a new one), and
    /// <paramref name="isDisposed"/> reads the session's disposed flag under that gate so a late add fails fast.
    /// </summary>
    public BundledMediaSessionTrackMutation(
        object gate,
        Func<bool> isDisposed,
        BundledTrackRouter router,
        BundledOutboundPipeline outbound,
        BundledVideoTrackSet video,
        BundledAudioTrackSet audioTracks,
        BundledOutboundSsrcTracker outboundSsrcs,
        List<BundledVideoTrack> deactivatedVideoTracks,
        BundledMediaSessionOptions options,
        ILoggerFactory loggerFactory,
        string primaryAudioMid,
        Action<string, BundledVideoTrack, bool> wireVideoTrackEvents,
        Action<string, RtpPacket> raiseAudioTrackReceivedGuarded,
        BundledStreamAttribution attribution)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _audioTracks = audioTracks ?? throw new ArgumentNullException(nameof(audioTracks));
        _outboundSsrcs = outboundSsrcs ?? throw new ArgumentNullException(nameof(outboundSsrcs));
        _deactivatedVideoTracks = deactivatedVideoTracks ?? throw new ArgumentNullException(nameof(deactivatedVideoTracks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _primaryAudioMid = primaryAudioMid ?? throw new ArgumentNullException(nameof(primaryAudioMid));
        _wireVideoTrackEvents = wireVideoTrackEvents ?? throw new ArgumentNullException(nameof(wireVideoTrackEvents));
        _raiseAudioTrackReceivedGuarded = raiseAudioTrackReceivedGuarded ?? throw new ArgumentNullException(nameof(raiseAudioTrackReceivedGuarded));
        _attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
    }

    /// <summary>Adds a video track live (see <see cref="BundledMediaSession.AddVideoTrack"/>).</summary>
    public void AddVideoTrack(BundledTrackConfig video)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentException.ThrowIfNullOrEmpty(video.Mid, nameof(video));

        lock (_gate)
        {
            if (_isDisposed())
                throw new InvalidOperationException("Cannot add a video track to a disposed bundled media session.");
            if (_video.Find(video.Mid) is not null)
                throw new InvalidOperationException($"A video track with MID '{video.Mid}' already exists on this bundle.");
            if (_audioTracks.Contains(video.Mid))
                throw new InvalidOperationException($"An audio track with MID '{video.Mid}' already exists on this bundle.");

            // 1. Extend the demux boundary FIRST: inbound packets for the new MID are now accepted (rather than
            //    rejected as an unknown MID) and, until the sink is registered below, cleanly dropped/counted.
            _router.AddKnownMid(video.Mid);

            // Every step from here on is undone if a later one throws (#161 P2-11). A rejected config used to
            // leave the MID known and its outbound sender registered — a bundle sending on a MID that has no
            // track, with its SSRCs claimed, so even a corrected retry failed with "already registered".
            BundledVideoTrack? track = null;
            try
            {
                // 2. Register the outbound sender(s) for the MID (simulcast: one per a=rid encoding; plain: one,
                //    with RTX when negotiated) — identical to the ctor path — and build the track that will be
                //    its sink. BuildVideoTrack itself is transactional: it registers nothing until the track is
                //    built, and unwinds its own registrations if a later one fails.
                track = BundledMediaSessionComposition.BuildVideoTrack(_options, video, _outbound, _loggerFactory);

                // 3. Wire the track's inbound frame / key-frame events. A live-added track is never the primary,
                //    so it fires only the mid-tagged VideoTrackFrameReceived, leaving the mid-less facade on the
                //    ctor primary.
                _wireVideoTrackEvents(video.Mid, track, false);

                // 4. Register the inbound router sink LAST, so no packet can hit a half-built track: only now can
                //    an inbound datagram for the new MID reach a live, fully-wired track.
                _router.RegisterTrack(video.Mid, track.OnRtpPacket);

                // 5. Publish to the video set so the send API and RTCP feedback fan-out find it.
                if (!_video.TryAdd(video.Mid, track))
                {
                    // Lost a race we hold the gate against — should be unreachable. Surface it rather than leak
                    // a half-registered track; the catch below unwinds everything this call did.
                    throw new InvalidOperationException($"A video track with MID '{video.Mid}' already exists on this bundle.");
                }
            }
            catch
            {
                // Mirrors a deactivate: inbound sink first, then the outbound sender(s), then the track object.
                // The MID stays in the demultiplexer's known set on purpose — that is exactly the state a
                // deactivated track leaves behind (packets for it are cleanly dropped and counted), AddKnownMid
                // is idempotent, and a retry with a corrected config re-adds it.
                _router.UnregisterTrack(video.Mid);
                _outbound.UnregisterTrack(video.Mid);
                track?.Dispose();
                throw;
            }

            // 6. Record the track's SSRCs as live (RFC 3550 §8.1) so a later renegotiation allocates around them.
            _outboundSsrcs.Add(video.Mid, video);

            // 7. Extend the metric attribution the same way the constructor seeds it, so the new track's inbound
            //    sources resolve their negotiated clock/kind and its outbound SSRCs are attributed to this MID.
            //    Both maps were construction-time snapshots before, which left every live-added track's inbound
            //    jitter on an inferred clock with an unknown kind, and its outbound RTT/loss unattributed.
            _attribution.TrackAdded(video, BundledStreamKind.Video, BundledMediaSessionComposition.VideoRtpClockRate);
        }
    }

    /// <summary>Deactivates a video track live (see <see cref="BundledMediaSession.SetVideoTrackInactive"/>).</summary>
    public void SetVideoTrackInactive(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);

        lock (_gate)
        {
            if (_isDisposed())
                return; // teardown in progress — every track is disposed by DisposeAsync.

            // Inbound first: no further datagram for this MID reaches a sink (the router drops/counts it instead).
            _router.UnregisterTrack(mid);
            // Then outbound: every RID layer registered under the MID is removed, so no further frame is sent.
            _outbound.UnregisterTrack(mid);
            // Drop it from the set, but do NOT dispose here: the receive loop may be inside its OnRtpPacket (a
            // loss-triggered feedback send reads its lifetime token), and a live dispose would throw
            // ObjectDisposedException on the loop → whole-bundle teardown. Defer to DisposeAsync (HARD-C6 drain).
            if (_video.Remove(mid) is { } removed)
                _deactivatedVideoTracks.Add(removed);
            // Release the track's SSRCs from the live bookkeeping so a later renegotiation may reuse them (the
            // per-SSRC SRTP context is gone with the track). No-op when the MID was already inactive (idempotent).
            _outboundSsrcs.Remove(mid);
            // And drop the outbound metric attribution for those SSRCs: the track no longer sends, so a report
            // block still naming one belongs to a stream that is gone.
            _attribution.TrackRemoved(mid);
        }
    }

    /// <summary>Adds an additional audio track live (see <see cref="BundledMediaSession.AddAudioTrack"/>).</summary>
    public void AddAudioTrack(BundledTrackConfig audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrEmpty(audio.Mid, nameof(audio));

        // Anchor protection: the primary audio m-line anchors ICE/DTLS and is the mid-less audio path — it is never
        // an additional/diffable track, so it can neither be added here nor deactivated.
        if (string.Equals(audio.Mid, _primaryAudioMid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot add an additional audio track with the primary anchor MID '{_primaryAudioMid}'.");

        lock (_gate)
        {
            if (_isDisposed())
                throw new InvalidOperationException("Cannot add an audio track to a disposed bundled media session.");
            if (_audioTracks.Contains(audio.Mid))
                throw new InvalidOperationException($"An audio track with MID '{audio.Mid}' already exists on this bundle.");
            if (_video.Find(audio.Mid) is not null)
                throw new InvalidOperationException($"A video track with MID '{audio.Mid}' already exists on this bundle.");

            var mid = audio.Mid;
            // 1. Extend the demux boundary FIRST (mirrors AddVideoTrack): inbound packets for the new MID are now
            //    accepted rather than rejected as unknown; until the sink exists below they are cleanly dropped.
            _router.AddKnownMid(mid);

            // Everything below unwinds as one unit if any step throws (#161 P2-11).
            try
            {
                // 2. Register the symmetric outbound sender for the MID (identical to the ctor path) so the same
                //    session can emit on the MID over a loopback peer; there is no public N-audio send API in
                //    this slice.
                _outbound.RegisterTrack(mid, BundledMediaSessionComposition.BuildOutboundTrack(_options, audio));

                // 3. Register the inbound router sink LAST: a bare receive sink dispatching on the mid-tagged
                //    event, guarded so a throwing subscriber never tears down the shared receive loop (K3). DTMF
                //    is NOT reassembled for an additional audio track (stays on the primary anchor).
                _router.RegisterTrack(mid, packet => _raiseAudioTrackReceivedGuarded(mid, packet));

                // 4. Publish the MID to the set so the accessors/send seam find it. The (unreachable — we hold
                //    the gate) race surfaces as an exception, and the catch unwinds the partial wiring.
                if (!_audioTracks.TryAdd(mid))
                    throw new InvalidOperationException($"An audio track with MID '{mid}' already exists on this bundle.");
            }
            catch
            {
                _router.UnregisterTrack(mid);
                _outbound.UnregisterTrack(mid);
                throw;
            }

            // 5. Record the track's SSRC as live (RFC 3550 §8.1) so a later renegotiation allocates around it. The
            //    tracker keys per MID and reads the config's SSRC(s); an audio config contributes just its one SSRC.
            _outboundSsrcs.Add(mid, audio);

            // 6. Extend the metric attribution, as the constructor does for the tracks it composes: the inbound
            //    clock/kind/MID for this track's payload type (first registration wins — a payload type shared
            //    with a live track keeps its existing attribution) and its outbound SSRC identity.
            _attribution.TrackAdded(audio, BundledStreamKind.Audio, audio.ClockRate > 0 ? (uint)audio.ClockRate : 0u);
        }
    }

    /// <summary>Deactivates an additional audio track live (see <see cref="BundledMediaSession.SetAudioTrackInactive"/>).</summary>
    public void SetAudioTrackInactive(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);

        // Anchor protection: the primary audio m-line is the transport anchor and the mid-less audio path — never
        // deactivate it, whatever a diff computed. A no-op keeps the transport and inbound audio intact.
        if (string.Equals(mid, _primaryAudioMid, StringComparison.Ordinal))
            return;

        lock (_gate)
        {
            if (_isDisposed())
                return; // teardown in progress — every registration is dropped by DisposeAsync.

            // Inbound first: no further datagram for this MID reaches a sink (the router drops/counts it instead).
            _router.UnregisterTrack(mid);
            // Then outbound: the symmetric sender registered under the MID is removed, so no further frame is sent.
            _outbound.UnregisterTrack(mid);
            // Drop the MID from the set. Audio has no per-track object to defer-dispose (the sink was on the router,
            // the sender on the pipeline — both unregistered above), so there is no HARD-C6 drain concern.
            _audioTracks.Remove(mid);
            // Release the track's SSRC from the live bookkeeping so a later renegotiation may reuse it. No-op when
            // the MID was already inactive (idempotent).
            _outboundSsrcs.Remove(mid);
            // Same for the outbound metric attribution (see SetVideoTrackInactive); the inbound clock entry stays.
            _attribution.TrackRemoved(mid);
        }
    }
}
