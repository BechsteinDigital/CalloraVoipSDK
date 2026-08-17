using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Applies a second offer/answer cycle to a <em>running</em> <see cref="BundledMediaSession"/> (RFC 8829
/// renegotiation, 4.7.0): it diffs the newly-exchanged descriptions against the live ones and applies the
/// track-set delta — adding a track for each newly-negotiated video or additional-audio MID and deactivating one
/// for each such MID the re-offer dropped — <b>without</b> rebuilding the transport, DTLS, ICE, or SRTP context.
/// The PRIMARY audio m-line (the transport anchor) is never diffed, added, or deactivated (it carries ICE/DTLS).
/// There is no ICE restart: a changed ICE ufrag/pwd on the shared audio m-line is rejected (a documented
/// limitation), since re-keying the transport is not what this path does.
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

    /// <summary>Creates a renegotiator that logs via <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">Builds the diagnostic logger for the diff.</param>
    /// <param name="opaqueVideoFrames">
    /// The owning peer's <see cref="WebRtcPeerOptions.OpaqueVideoFrames"/> policy, stamped onto every video track
    /// this renegotiator adds so a mid-call track matches the session's existing ones.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public WebRtcRenegotiator(ILoggerFactory loggerFactory, bool opaqueVideoFrames)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<WebRtcRenegotiator>();
        _opaqueVideoFrames = opaqueVideoFrames;
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
    /// <exception cref="InvalidOperationException">No answer could be negotiated, or the re-offer requests an unsupported ICE restart.</exception>
    public SdpSessionDescription NegotiateAnswerAndApply(
        BundledMediaSession session,
        SdpSessionDescription remote,
        WebRtcRenegotiationAnswerContext answerContext)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(answerContext);

        var result = answerContext.Negotiator.NegotiateAnswer(
            remote, answerContext.Local, answerContext.AudioCodecs, SdpMediaDirection.SendRecv, answerContext.MediaOptions);
        if (!result.Success || result.Answer is null)
        {
            // A failed re-answer leaves the running session intact (the old tracks keep flowing) — this throws before
            // ComputeDiff/Apply. The peer catches this, rolls signalling back to Stable, and re-throws, so the live
            // session stays usable and a later renegotiation can be attempted.
            throw new InvalidOperationException("Could not negotiate an answer for the renegotiated remote description.");
        }

        Apply(session, ComputeDiff(session, result.Answer, remote));
        return result.Answer;
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
    /// <exception cref="InvalidOperationException">
    /// The new descriptions request an ICE restart (a changed ICE ufrag/pwd on the shared audio m-line) — this
    /// path does not rebuild the transport, so an ICE restart is not supported (dispose and re-create the peer).
    /// </exception>
    public WebRtcRenegotiationDiff ComputeDiff(
        BundledMediaSession session,
        SdpSessionDescription newLocalDescription,
        SdpSessionDescription newRemoteDescription)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(newLocalDescription);
        ArgumentNullException.ThrowIfNull(newRemoteDescription);

        // Reject an ICE restart (RFC 8829 §5.3.1: a fresh ufrag/pwd on the transport-anchoring audio m-line):
        // this path only diffs the track set on the existing transport, so a re-keyed transport is out of scope.
        RejectIceRestart(session, newRemoteDescription);

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

    // Rejects a re-offer that rotates the ICE credentials on the transport-anchoring audio m-line (RFC 8829
    // §5.3.1 ICE restart): the shared transport keeps the ICE ufrag/pwd it was built with, and re-keying it is
    // not part of the track-diff path. A re-offer that keeps the same credentials (the common mid-call
    // add/remove-a-track case) passes through. The remote audio section carries the peer's ICE credentials
    // (rtcp-mux BUNDLE shares the one transport, RFC 8843), so it is the one to compare.
    private static void RejectIceRestart(BundledMediaSession session, SdpSessionDescription newRemoteDescription)
    {
        var newRemoteAudio = newRemoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci));
        if (newRemoteAudio?.IceUfrag is not { } newUfrag)
            return; // No ICE credentials in the re-offer to compare — nothing signals a restart.

        // The session exposes the remote ICE credentials it was built with; a mismatch is an ICE restart request.
        if (session.RemoteIceUfrag is { } currentUfrag
            && !string.Equals(currentUfrag, newUfrag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ICE restart is not supported on a running WebRTC peer: the re-offer rotates the ICE ufrag on the " +
                "shared transport. Dispose this peer and create a new one to restart ICE.");
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
/// <param name="MediaOptions">The media options (BUNDLE, DTLS, ICE, track set) for the answer.</param>
internal sealed record WebRtcRenegotiationAnswerContext(
    ISdpOfferAnswerNegotiator Negotiator,
    IPEndPoint Local,
    IReadOnlyList<SdpCodecDefinition> AudioCodecs,
    SdpMediaOptions MediaOptions);
