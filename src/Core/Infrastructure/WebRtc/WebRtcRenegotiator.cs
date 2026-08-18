using System.Net;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Applies a second offer/answer cycle to a <em>running</em> <see cref="BundledMediaSession"/> (RFC 8829
/// renegotiation, 4.7.0): it diffs the newly-exchanged descriptions against the live ones and applies the
/// track-set delta — adding a track for each newly-negotiated video or additional-audio MID and deactivating one
/// for each such MID the re-offer dropped — <b>without</b> rebuilding the transport, DTLS, ICE, or SRTP context.
/// The PRIMARY audio m-line (the transport anchor) is never diffed, added, or deactivated (it carries ICE/DTLS).
/// <para>
/// It also carries an <b>ICE restart</b> (#226, RFC 8445 §9 / RFC 8829 §5.3.1): rotated ICE credentials on the
/// transport-anchoring m-line replace the session's ICE agent in place, so a peer that changed networks
/// re-establishes connectivity instead of the call dropping. Nothing above ICE is rebuilt — the socket, the DTLS
/// association and every SRTP context survive, which is the whole point of a restart. The local ICE credentials
/// live here for the same reason: an ICE restart is the only thing that ever rotates them, and a restart is a
/// renegotiation.
/// </para>
/// <para>
/// SSRC allocation (RFC 3550 §8.1): the pool is seeded from <see cref="BundledMediaSession.OutboundSsrcs"/> —
/// the SSRCs live on the session at diff time — because a WebRTC local description carries no <c>a=ssrc</c>
/// lines (MID-based demux, RFC 9143), so the SSRCs are internal to the session and cannot be read back from
/// the SDP. Each added track's primary/per-encoding/RTX SSRCs are allocated distinct from that pool, so a new
/// track never collides the per-SSRC SRTP context of a running one.
/// </para>
/// <para>
/// Threading (K3): the diff is computed from immutable description snapshots the caller passes in; the apply
/// step calls <see cref="BundledMediaSession.AddVideoTrack"/> / <see cref="BundledMediaSession.SetVideoTrackInactive"/>,
/// which are already serialised against the receive loop by the session's own track-mutation gate. The caller
/// must NOT hold the peer's signalling lock across <see cref="Apply"/> — the session mutations take their own
/// gate and must run outside the peer lock (snapshot under the lock, mutate outside), matching the peer's
/// existing session-build pattern.
/// </para>
/// </summary>
internal sealed class WebRtcRenegotiator
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcRenegotiator> _logger;

    // The owning peer's opaque-video-frames policy (#223, ADR-068). Held for the renegotiator's lifetime because
    // it is a peer-level transport policy that no re-offer can change: a track ADDED mid-call must get the same
    // payload format as the tracks built at session time, or renegotiation would quietly hand an end-to-end
    // encrypted peer a clear-media track that reads ciphertext.
    private readonly bool _opaqueVideoFrames;

    // Raised after an ICE restart was applied, so the owning peer can model it as a connection-state transition
    // (back to Connecting) instead of leaving a peer that had already fallen to Failed there forever.
    private readonly Action? _onIceRestarted;

    // The local ICE credentials currently advertised. Mutable because an ICE restart rotates them
    // (RFC 8445 §9.1.1.1) on a peer whose configuration — and socket — stay exactly what they were. Guarded by
    // its own gate: the peer reads it while building any description, on whichever thread signalling arrives on.
    private readonly object _iceGate = new();
    private SdpIceParameters _localIce;

    /// <summary>Creates a renegotiator that logs via <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">Builds the diagnostic logger for the diff.</param>
    /// <param name="opaqueVideoFrames">
    /// The owning peer's <see cref="WebRtcPeerOptions.OpaqueVideoFrames"/> policy, stamped onto every video track
    /// this renegotiator adds so a mid-call track matches the session's existing ones.
    /// </param>
    /// <param name="localIce">The peer's configured local ICE credentials and candidates — the starting value.</param>
    /// <param name="onIceRestarted">Invoked after an applied ICE restart so the peer can transition its state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> or <paramref name="localIce"/> is <see langword="null"/>.</exception>
    public WebRtcRenegotiator(
        ILoggerFactory loggerFactory,
        bool opaqueVideoFrames,
        SdpIceParameters localIce,
        Action? onIceRestarted = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<WebRtcRenegotiator>();
        _opaqueVideoFrames = opaqueVideoFrames;
        _localIce = localIce ?? throw new ArgumentNullException(nameof(localIce));
        _onIceRestarted = onIceRestarted;
    }

    /// <summary>
    /// The local ICE credentials and configured candidates to advertise in the next description. Follows an ICE
    /// restart, so an answer to a restart offer carries the fresh ufrag/pwd the peer will authenticate against.
    /// </summary>
    public SdpIceParameters LocalIceParameters
    {
        get { lock (_iceGate) { return _localIce; } }
    }

    /// <summary>
    /// Runs the media half of a second offer/answer cycle for the answerer: negotiates a fresh answer to
    /// <paramref name="remote"/>, then computes the track-set diff (video and additional-audio) against
    /// <paramref name="session"/> and applies it live. The offerer path needs no negotiation (its re-offer is
    /// already the local description), so it calls
    /// <see cref="ComputeDiff"/> + <see cref="Apply"/> directly. The signalling-state machine and description
    /// bookkeeping stay with the peer; this owns only the media work.
    /// </summary>
    /// <param name="session">The running media session to diff against and mutate.</param>
    /// <param name="remote">The newly-applied remote offer.</param>
    /// <param name="answerContext">The negotiator inputs (local endpoint, codecs, media options) to produce the answer.</param>
    /// <returns>The freshly negotiated answer model.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No answer could be negotiated.</exception>
    public async Task<SdpSessionDescription> NegotiateAnswerAndApplyAsync(
        BundledMediaSession session,
        SdpSessionDescription remote,
        WebRtcRenegotiationAnswerContext answerContext)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(answerContext);

        // Rotate our own credentials FIRST when the re-offer restarts ICE (RFC 8445 §9.1.1.1: the answerer to a
        // restart offer must generate new ones too), because the answer built below has to advertise them — the
        // peer authenticates its checks against what our answer says, not against what we used before.
        var restart = DetectIceRestart(session, remote);
        if (restart is not null)
            RotateLocalIce();

        var result = answerContext.Negotiator.NegotiateAnswer(
            remote, answerContext.Local, answerContext.AudioCodecs, SdpMediaDirection.SendRecv, answerContext.MediaOptions());
        if (!result.Success || result.Answer is null)
        {
            // A failed re-answer leaves the running session intact (the old tracks keep flowing) — this throws before
            // ComputeDiff/Apply. The peer catches this, rolls signalling back to Stable, and re-throws, so the live
            // session stays usable and a later renegotiation can be attempted.
            throw new InvalidOperationException("Could not negotiate an answer for the renegotiated remote description.");
        }

        Apply(session, ComputeDiff(session, result.Answer, remote));
        if (restart is not null)
            await RestartIceAsync(session, remote, restart).ConfigureAwait(false);
        return result.Answer;
    }

    /// <summary>
    /// The offerer half of a second cycle: applies the track-set diff between our re-offer and the peer's
    /// re-answer, and restarts ICE when that answer rotated the peer's ICE credentials (RFC 8445 §9). Our own
    /// credentials are <em>not</em> rotated here — the re-offer already advertised them and the peer is checking
    /// against those.
    /// </summary>
    /// <param name="session">The running media session to diff against and mutate.</param>
    /// <param name="newLocalDescription">Our re-offer.</param>
    /// <param name="newRemoteDescription">The peer's re-answer.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public async Task ApplyReAnswerAsync(
        BundledMediaSession session,
        SdpSessionDescription newLocalDescription,
        SdpSessionDescription newRemoteDescription)
    {
        ArgumentNullException.ThrowIfNull(session);

        var restart = DetectIceRestart(session, newRemoteDescription);
        Apply(session, ComputeDiff(session, newLocalDescription, newRemoteDescription));
        if (restart is not null)
            await RestartIceAsync(session, newRemoteDescription, restart).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the track-set delta (video and additional-audio) between a running session and a newly-exchanged
    /// offer/answer pair. <paramref name="newLocalDescription"/> is this peer's new description (the re-offer if
    /// offering, the new answer if answering) and <paramref name="newRemoteDescription"/> is the peer's new
    /// description; a video track is added only when both sides negotiate a video MID for sending (from the local
    /// sections), and an additional-audio track when either local-send/remote-receive or
    /// remote-send/local-receive is negotiated (from the local sections). Either is deactivated when a live MID is
    /// no longer negotiated. The primary audio anchor is never diffed. SSRCs for added tracks are allocated
    /// distinct from <paramref name="session"/>'s live outbound SSRCs and from each other (RFC 3550 §8.1).
    /// </summary>
    /// <param name="session">The running media session whose live MIDs and SSRCs anchor the diff.</param>
    /// <param name="newLocalDescription">This peer's new (re-negotiated) description.</param>
    /// <param name="newRemoteDescription">The peer's new description.</param>
    /// <returns>The delta to apply; <see cref="WebRtcRenegotiationDiff.IsEmpty"/> when nothing changed.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WebRtcRenegotiationDiff ComputeDiff(
        BundledMediaSession session,
        SdpSessionDescription newLocalDescription,
        SdpSessionDescription newRemoteDescription)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(newLocalDescription);
        ArgumentNullException.ThrowIfNull(newRemoteDescription);

        // An ICE restart is orthogonal to the track set and handled by the callers that own the cycle
        // (NegotiateAnswerAndApplyAsync / ApplyReAnswerAsync) — the diff below runs the same either way.

        // One SSRC pool for the whole diff, seeded from the live session (RFC 3550 §8.1). Video and audio adds both
        // draw from and grow it, so an added video track and an added audio track in the SAME diff never collide
        // each other or any running SSRC.
        var usedSsrcs = new HashSet<uint>(session.OutboundSsrcs);

        var (tracksToAdd, midsToDeactivate) = DiffVideo(session, newLocalDescription, newRemoteDescription, usedSsrcs);
        var (audioTracksToAdd, audioMidsToDeactivate) = DiffAudio(session, newLocalDescription, newRemoteDescription, usedSsrcs);

        _logger.LogDebug(
            "WebRTC renegotiation diff: video +{VideoAdd}/-{VideoRemove}, additional-audio +{AudioAdd}/-{AudioRemove}.",
            tracksToAdd.Count, midsToDeactivate.Count, audioTracksToAdd.Count, audioMidsToDeactivate.Count);

        return new WebRtcRenegotiationDiff
        {
            TracksToAdd = tracksToAdd,
            MidsToDeactivate = midsToDeactivate,
            AudioTracksToAdd = audioTracksToAdd,
            AudioMidsToDeactivate = audioMidsToDeactivate,
        };
    }

    // The video half of the track-set diff: iterate the new LOCAL video sections (this peer is the sender for an
    // outbound video track), building a config for each MID both sides negotiated for sending. A MID not already
    // live is an add; a live MID no longer negotiated is a deactivate.
    private (IReadOnlyList<BundledTrackConfig> Add, IReadOnlyList<string> Deactivate) DiffVideo(
        BundledMediaSession session,
        SdpSessionDescription newLocalDescription,
        SdpSessionDescription newRemoteDescription,
        ISet<uint> usedSsrcs)
    {
        var liveMids = new HashSet<string>(session.VideoMids, StringComparer.Ordinal);
        var newSendingMids = new HashSet<string>(StringComparer.Ordinal);
        var tracksToAdd = new List<BundledTrackConfig>();
        foreach (var localVideo in newLocalDescription.Media.Where(m => m.MediaType.Equals("video", Ci)))
        {
            if (string.IsNullOrEmpty(localVideo.Mid))
                continue;

            // TryBuildVideoTrack returns null unless BOTH sides negotiated this MID for sending, so it is the one
            // authority for "is this MID a sending track after the re-offer". It also allocates the added track's
            // SSRCs distinct from usedSsrcs (which starts at the live session SSRCs) and grows the pool, so two
            // tracks added in one diff never collide either (RFC 3550 §8.1).
            var config = WebRtcSessionFactory.TryBuildVideoTrack(
                localVideo, newRemoteDescription, usedSsrcs, _loggerFactory, _opaqueVideoFrames);
            if (config is null)
                continue;

            newSendingMids.Add(config.Mid);
            // Only MIDs not already live are new tracks to add; a MID that stays sending keeps its running track
            // (its SSRCs and per-SSRC SRTP context are unchanged — we do not rebuild an unchanged track).
            if (!liveMids.Contains(config.Mid))
                tracksToAdd.Add(config);
        }

        // A live MID that the re-offer no longer negotiates for sending (dropped, rejected with port 0, made
        // inactive, or turned recvonly on either side) is deactivated.
        var midsToDeactivate = liveMids.Where(mid => !newSendingMids.Contains(mid)).ToArray();
        return (tracksToAdd, midsToDeactivate);
    }

    // The additional-audio half of the track-set diff (4.7.0: N audio m-lines — the SFU pattern), symmetric to the
    // video half and iterating the new LOCAL audio sections. TryBuildAudioTrack pairs each section to the remote
    // description and materialises it for either negotiated direction. The PRIMARY audio m-line — the transport
    // anchor — is NEVER diffed: it is skipped by its MID (session.PrimaryAudioMid), so a renegotiation can never
    // add, drop, or re-key the anchor.
    private (IReadOnlyList<BundledTrackConfig> Add, IReadOnlyList<string> Deactivate) DiffAudio(
        BundledMediaSession session,
        SdpSessionDescription newLocalDescription,
        SdpSessionDescription newRemoteDescription,
        ISet<uint> usedSsrcs)
    {
        var anchorMid = session.PrimaryAudioMid;
        var liveMids = new HashSet<string>(session.AudioMids, StringComparer.Ordinal);
        var newNegotiatedMids = new HashSet<string>(StringComparer.Ordinal);
        var audioTracksToAdd = new List<BundledTrackConfig>();
        foreach (var localAudio in newLocalDescription.Media.Where(m => m.MediaType.Equals("audio", Ci)))
        {
            if (string.IsNullOrEmpty(localAudio.Mid))
                continue;
            // Anchor protection: never diff the primary audio m-line — it anchors ICE/DTLS and rides the mid-less
            // audio path, and the session refuses to add/deactivate it anyway (belt-and-suspenders here).
            if (string.Equals(localAudio.Mid, anchorMid, StringComparison.Ordinal))
                continue;

            // TryBuildAudioTrack returns null unless at least one flow direction is negotiated on this MID, so it
            // is the authority for "is this an additional audio track after the re-offer". It allocates the added
            // track's SSRC distinct from usedSsrcs (seeded from the live session, shared with the video adds) and
            // grows the pool, so an added audio track never collides an added video track or a running SSRC.
            var config = WebRtcSessionFactory.TryBuildAudioTrack(
                localAudio, newRemoteDescription, usedSsrcs, _loggerFactory);
            if (config is null)
                continue;

            newNegotiatedMids.Add(config.Mid);
            if (!liveMids.Contains(config.Mid))
                audioTracksToAdd.Add(config);
        }

        // A live additional-audio MID the re-offer no longer negotiates in either direction is deactivated. The
        // anchor is not in liveMids (session.AudioMids excludes it), so it can never appear here.
        var audioMidsToDeactivate = liveMids.Where(mid => !newNegotiatedMids.Contains(mid)).ToArray();
        return (audioTracksToAdd, audioMidsToDeactivate);
    }

    /// <summary>
    /// Applies a computed diff to the live session: deactivates dropped tracks first (freeing their MIDs/SSRCs),
    /// then adds the new ones. Both operations run on the session's own track-mutation gate — the caller must not
    /// hold the peer signalling lock across this call. A no-op for an empty diff.
    /// </summary>
    /// <param name="session">The running session to mutate.</param>
    /// <param name="diff">The delta computed by <see cref="ComputeDiff"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public void Apply(BundledMediaSession session, WebRtcRenegotiationDiff diff)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(diff);

        // Deactivate first (video and audio): it releases each MID's SSRCs, so an add that reuses a just-freed MID
        // (a track toggled off then a new one in the same MID slot) never trips the "MID already exists" guard.
        // Both deactivations are idempotent and the primary audio anchor is never in either list (ComputeDiff).
        foreach (var mid in diff.MidsToDeactivate)
            session.SetVideoTrackInactive(mid);
        foreach (var mid in diff.AudioMidsToDeactivate)
            session.SetAudioTrackInactive(mid);

        // Then add each new track (video then audio). Their SSRCs were already allocated distinct from the live
        // session and from each other (one shared usedSsrcs pool in ComputeDiff) and from any track just deactivated
        // above, so the per-SSRC SRTP context stays collision-free across both kinds.
        foreach (var config in diff.TracksToAdd)
            session.AddVideoTrack(config);
        foreach (var config in diff.AudioTracksToAdd)
            session.AddAudioTrack(config);
    }

    // Detects a re-negotiation that rotates the peer's ICE credentials on the transport-anchoring audio m-line
    // (RFC 8445 §9.1.1.1 / RFC 8829 §5.3.1) and returns the section carrying them; null means no restart, which
    // is the common mid-call add/remove-a-track case. The remote AUDIO section is the one to compare: rtcp-mux
    // BUNDLE runs the whole group over that one transport (RFC 8843), so it carries the peer's ICE credentials.
    private static SdpMediaDescription? DetectIceRestart(
        BundledMediaSession session, SdpSessionDescription newRemoteDescription)
    {
        var newRemoteAudio = newRemoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci));
        if (newRemoteAudio is null)
            return null;

        // Both halves matter — a restart may rotate either the ufrag or the pwd (RFC 8445 §9.1.1.1) — and the
        // shared detector also rules out the two look-alikes: a first negotiation, and ICE being removed rather
        // than restarted.
        return IceRestartDetector.IsRestart(
            session.RemoteIceUfrag, session.RemoteIcePwd, newRemoteAudio.IceUfrag, newRemoteAudio.IcePwd)
            ? newRemoteAudio
            : null;
    }

    // Applies a detected ICE restart to the running session: build the new ICE view from the re-negotiated remote
    // section and the local credentials now in force, hand it to the session (which swaps the agent on the live
    // socket), and let the peer model it as a state transition.
    private async Task RestartIceAsync(
        BundledMediaSession session, SdpSessionDescription remote, SdpMediaDescription remoteAudio)
    {
        // The peer's new transport address and check-list candidates, resolved exactly as the initial session
        // build resolves them — including the session-level c= line fallback, since a re-offer may carry the new
        // address only there. A restart usually accompanies a network change, so these normally all moved.
        var remoteEndPoint = WebRtcRemoteEndPoint.Resolve(remoteAudio, remote.ConnectionAddress);
        if (remoteEndPoint is null)
        {
            // Nothing to check against. Leave the running agent alone rather than replace it with one that has no
            // remote: media on the previously selected pair is still the best outcome available here.
            _logger.LogWarning(
                "The re-negotiation rotated the peer's ICE credentials but carried no usable remote address; " +
                "keeping the running ICE agent.");
            return;
        }

        var remoteCandidates = WebRtcSessionFactory.RemoteCandidates(remoteAudio);
        if (remoteCandidates.Count == 0)
            remoteCandidates = [new IceRemoteCandidate(remoteEndPoint, WebRtcSessionFactory.DefaultCandidatePriority)];

        var localIce = LocalIceParameters;
        await session.RestartIceAsync(new IceMediaParameters(
            remoteEndPoint,
            IceEnabled: true,
            // The role is deliberately carried over: a restart re-runs the checks, it does not redetermine which
            // agent controls them (RFC 8445 §9.1.1.1 — a role switch would need a fresh role negotiation).
            session.IceControlling,
            LocalIceUfrag: localIce.Ufrag,
            LocalIcePwd: localIce.Pwd,
            RemoteIceUfrag: remoteAudio.IceUfrag,
            RemoteIcePwd: remoteAudio.IcePwd)
        {
            RemoteCandidates = remoteCandidates,
        }).ConfigureAwait(false);

        _logger.LogInformation(
            "ICE restarted on the running peer (RFC 8445 §9): {CandidateCount} remote candidate(s), same transport.",
            remoteCandidates.Count);
        _onIceRestarted?.Invoke();
    }

    // Fresh local short-term credentials for a restart (RFC 8445 §9.1.1.1 requires BOTH to change). The
    // configured candidates and ice-options carry over — a restart re-runs the checks over the same socket, so
    // the local candidates advertised for it are still the ones we have.
    private void RotateLocalIce()
    {
        lock (_iceGate)
        {
            _localIce = new SdpIceParameters
            {
                Ufrag = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
                Pwd = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                Options = _localIce.Options,
                Candidates = _localIce.Candidates,
            };
        }
    }
}

/// <summary>
/// The negotiator inputs a WebRTC peer hands the renegotiator to produce a fresh answer during a second
/// offer/answer cycle (answerer path): the SDP offer/answer negotiator, the bound local media endpoint, the
/// peer's audio codecs, and the media options assembled for the answer. Bundled so the renegotiator owns the
/// answer negotiation without depending on the peer's internals.
/// </summary>
/// <param name="Negotiator">The SDP offer/answer negotiator that produces the answer.</param>
/// <param name="Local">The bound local media endpoint the answer advertises.</param>
/// <param name="AudioCodecs">The peer's offered audio codecs.</param>
/// <param name="MediaOptions">
/// Builds the media options (BUNDLE, DTLS, ICE, track set) for the answer. A factory rather than a value because
/// an ICE restart rotates the local credentials before the answer is negotiated, and the answer must carry the
/// rotated ones — a value captured at the call site would still hold the retired ufrag.
/// </param>
internal sealed record WebRtcRenegotiationAnswerContext(
    ISdpOfferAnswerNegotiator Negotiator,
    IPEndPoint Local,
    IReadOnlyList<SdpCodecDefinition> AudioCodecs,
    Func<SdpMediaOptions> MediaOptions);
