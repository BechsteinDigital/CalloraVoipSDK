using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Applies a second offer/answer cycle to a <em>running</em> <see cref="BundledMediaSession"/> (RFC 8829
/// renegotiation, 4.7.0 P3b-3): it diffs the newly-exchanged descriptions against the live ones and applies
/// only the video-track delta — adding a track for each newly-negotiated video MID and deactivating one for
/// each MID the re-offer dropped — <b>without</b> rebuilding the transport, DTLS, ICE, or SRTP context. There
/// is no ICE restart: a changed ICE ufrag/pwd on the shared audio m-line is rejected (a documented limitation),
/// since re-keying the transport is not what this path does.
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

    /// <summary>Creates a renegotiator that logs via <paramref name="loggerFactory"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public WebRtcRenegotiator(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<WebRtcRenegotiator>();
    }

    /// <summary>
    /// Runs the media half of a second offer/answer cycle for the answerer: negotiates a fresh answer to
    /// <paramref name="remote"/>, then computes the video-track diff against <paramref name="session"/> and applies
    /// it live. The offerer path needs no negotiation (its re-offer is already the local description), so it calls
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
    /// Computes the video-track delta between a running session and a newly-exchanged offer/answer pair.
    /// <paramref name="newLocalDescription"/> is this peer's new description (the re-offer if offering, the new
    /// answer if answering) and <paramref name="newRemoteDescription"/> is the peer's new description; a track
    /// is added only when both sides negotiate a video MID for sending, and deactivated when a live MID is no
    /// longer negotiated for sending. SSRCs for added tracks are allocated distinct from <paramref name="session"/>'s
    /// live outbound SSRCs (RFC 3550 §8.1).
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

        // The MIDs currently sending on the live session (P2b: the built video tracks), so an add is only for a
        // MID the session does not already carry and a removal only for one it does.
        var liveMids = new HashSet<string>(session.VideoMids, StringComparer.Ordinal);

        // The new local video sections that are negotiated for sending, paired with their peer section by MID —
        // this is the target set of MIDs that should be live after the re-offer.
        var newSendingMids = new HashSet<string>(StringComparer.Ordinal);
        var tracksToAdd = new List<BundledTrackConfig>();
        var usedSsrcs = new HashSet<uint>(session.OutboundSsrcs);
        foreach (var localVideo in newLocalDescription.Media.Where(m => m.MediaType.Equals("video", Ci)))
        {
            if (string.IsNullOrEmpty(localVideo.Mid))
                continue;

            // TryBuildVideoTrack returns null unless BOTH sides negotiated this MID for sending, so it is the one
            // authority for "is this MID a sending track after the re-offer". It also allocates the added track's
            // SSRCs distinct from usedSsrcs (which starts at the live session SSRCs) and grows the pool, so two
            // tracks added in one diff never collide either (RFC 3550 §8.1).
            var config = WebRtcSessionFactory.TryBuildVideoTrack(
                localVideo, newRemoteDescription, usedSsrcs, _loggerFactory);
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

        _logger.LogDebug(
            "WebRTC renegotiation diff: {AddCount} video track(s) to add, {RemoveCount} to deactivate.",
            tracksToAdd.Count, midsToDeactivate.Length);

        return new WebRtcRenegotiationDiff
        {
            TracksToAdd = tracksToAdd,
            MidsToDeactivate = midsToDeactivate,
        };
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

        // Deactivate first: it releases the MID's SSRCs, so an add that reuses a just-freed MID (a track toggled
        // off then a new one in the same MID slot) never trips the "MID already exists" guard. Idempotent.
        foreach (var mid in diff.MidsToDeactivate)
            session.SetVideoTrackInactive(mid);

        // Then add each new track. Their SSRCs were already allocated distinct from the live session (ComputeDiff),
        // and from any track just deactivated above, so AddVideoTrack's per-SSRC SRTP context stays collision-free.
        foreach (var config in diff.TracksToAdd)
            session.AddVideoTrack(config);
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
