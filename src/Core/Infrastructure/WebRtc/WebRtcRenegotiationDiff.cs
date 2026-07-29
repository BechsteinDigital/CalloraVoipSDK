using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The track-set delta a second offer/answer cycle applies to a running <see cref="BundledMediaSession"/>
/// (RFC 8829 renegotiation, 4.7.0): which video and additional-audio MIDs to add (each with a fully-built
/// <see cref="BundledTrackConfig"/> whose SSRCs are distinct from the running session) and which live video and
/// additional-audio MIDs to deactivate. Computed by <see cref="WebRtcRenegotiator"/> from the current live
/// descriptions and the newly-exchanged one; it is a pure description of the change, applied by the renegotiator on
/// the live session.
/// <para>
/// This delta covers only the track set: the shared transport, DTLS, ICE, and SRTP context are untouched
/// (no ICE restart). The PRIMARY audio m-line (the transport anchor) is never part of this delta — it is never
/// added, deactivated, or diffed. An empty diff (no adds, no removals) means the re-offer changed nothing.
/// </para>
/// </summary>
internal sealed record WebRtcRenegotiationDiff
{
    /// <summary>
    /// The new video tracks to add live, in the order they appear in the new local description. Each config
    /// already carries its MID, codec, payload type, and SSRC(s) — the latter allocated distinct from every
    /// SSRC live on the session at diff time — ready to hand to <see cref="BundledMediaSession.AddVideoTrack"/>.
    /// </summary>
    public IReadOnlyList<BundledTrackConfig> TracksToAdd { get; init; } = [];

    /// <summary>
    /// The MIDs of video tracks currently live on the session that the re-offer no longer negotiates for
    /// sending (absent, port-0/rejected, inactive, or recvonly on either side): to deactivate via
    /// <see cref="BundledMediaSession.SetVideoTrackInactive"/>. Deactivation is idempotent.
    /// </summary>
    public IReadOnlyList<string> MidsToDeactivate { get; init; } = [];

    /// <summary>
    /// The new ADDITIONAL audio tracks to add live (4.7.0: N audio m-lines — the SFU pattern), in the order they
    /// appear in the new remote description. Each config already carries its MID, codec, payload type, and a
    /// bundle-wide-distinct SSRC, ready to hand to <see cref="BundledMediaSession.AddAudioTrack"/>. Never contains
    /// the primary anchor MID.
    /// </summary>
    public IReadOnlyList<BundledTrackConfig> AudioTracksToAdd { get; init; } = [];

    /// <summary>
    /// The MIDs of additional audio tracks currently live on the session that the re-offer no longer negotiates
    /// for receiving: to deactivate via <see cref="BundledMediaSession.SetAudioTrackInactive"/>. Deactivation is
    /// idempotent and never targets the primary anchor.
    /// </summary>
    public IReadOnlyList<string> AudioMidsToDeactivate { get; init; } = [];

    /// <summary>Whether this diff changes nothing on the track set (no adds, no removals, video or audio).</summary>
    public bool IsEmpty =>
        TracksToAdd.Count == 0 && MidsToDeactivate.Count == 0
        && AudioTracksToAdd.Count == 0 && AudioMidsToDeactivate.Count == 0;
}
