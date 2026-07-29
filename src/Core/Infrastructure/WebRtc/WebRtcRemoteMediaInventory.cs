using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// The remote-track facts a <see cref="WebRtcPeerConnection"/> exposes to its receiver after applying a remote
/// description (RFC 8830 a=msid identity, whether the remote sends audio/video, and the per-m-line sending video
/// inventory). Computed by <see cref="FromRemoteDescription"/> from an applied remote description, so the same
/// derivation serves the first offer/answer and a renegotiation cycle.
/// </summary>
/// <param name="HasRemoteAudio">Whether the remote will send audio (an enabled sendrecv/sendonly audio m-line).</param>
/// <param name="HasRemoteVideo">Whether the remote will send video (an enabled sendrecv/sendonly video m-line).</param>
/// <param name="AudioMsid">The remote (primary) audio track's a=msid identity, or null.</param>
/// <param name="VideoMsid">The remote (first) video track's a=msid identity, or null.</param>
/// <param name="AudioTracks">Every <em>additional</em> remote audio m-line that will send to us — the ones beyond the primary anchor (4.7.0), in m-line order, each with its MID and a=msid.</param>
/// <param name="VideoTracks">Every remote video m-line that will send to us, in m-line order, each with its MID and a=msid.</param>
internal sealed record WebRtcRemoteMediaInventory(
    bool HasRemoteAudio,
    bool HasRemoteVideo,
    SdpMsid? AudioMsid,
    SdpMsid? VideoMsid,
    IReadOnlyList<RemoteAudioTrackInfo> AudioTracks,
    IReadOnlyList<RemoteVideoTrackInfo> VideoTracks)
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Derives the remote-track inventory from an applied remote description: the audio/video msid identities,
    /// the has-audio/has-video flags, one <see cref="RemoteAudioTrackInfo"/> per <em>additional</em> sending remote
    /// audio m-line (4.7.0: the ones beyond the primary anchor), and one <see cref="RemoteVideoTrackInfo"/> per
    /// sending remote video m-line (P2c). A disabled (port 0), inactive, or recvonly m-line never sends to us
    /// (RFC 8829 / RFC 3264), so it contributes no phantom track and does not set the corresponding has-* flag.
    /// </summary>
    public static WebRtcRemoteMediaInventory FromRemoteDescription(SdpSessionDescription remote)
    {
        var audioMedia = remote.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci));
        var videoMedia = remote.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
        // The additional audio tracks are the sending remote audio m-lines EXCEPT the primary (the first audio
        // m-line, the bundle transport anchor, surfaced via the mid-less audio path). Skipping the primary keeps
        // the 1-audio case producing zero additional tracks (byte-identical to the pre-4.7.0 receiver path).
        var audioTracks = remote.Media
            .Where(m => m.MediaType.Equals("audio", Ci) && !ReferenceEquals(m, audioMedia) && Sends(m))
            .Select(m => new RemoteAudioTrackInfo(m.Mid, m.Msid))
            .ToArray();
        var videoTracks = remote.Media
            .Where(m => m.MediaType.Equals("video", Ci) && Sends(m))
            .Select(m => new RemoteVideoTrackInfo(m.Mid, m.Msid))
            .ToArray();
        return new WebRtcRemoteMediaInventory(
            Sends(audioMedia), Sends(videoMedia), audioMedia?.Msid, videoMedia?.Msid, audioTracks, videoTracks);
    }

    // A remote m-line sends to us only when enabled (port != 0) and its negotiated direction includes sending
    // (sendrecv/sendonly) — RFC 8829 / RFC 3264 directionality.
    private static bool Sends(SdpMediaDescription? media)
        => media is { Disabled: false, Direction: SdpMediaDirection.SendRecv or SdpMediaDirection.SendOnly };
}
