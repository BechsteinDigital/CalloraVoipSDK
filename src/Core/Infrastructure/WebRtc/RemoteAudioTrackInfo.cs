using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// One remote audio m-line the peer will send to us (4.7.0: N audio tracks — the SFU pattern of one audio
/// stream per remote participant) — its MID (<c>a=mid</c>, RFC 5888) and its MediaStream/track identity
/// (<c>a=msid</c>, RFC 8830). The receiver materialises one public <see cref="WebRtc.RemoteTrack"/> per entry,
/// so several remote participants' audio streams stay separable. The <em>primary</em> audio m-line (the bundle
/// transport anchor) is surfaced separately via the mid-less audio path — this list holds only the additional ones.
/// </summary>
/// <param name="Mid">The remote audio m-line's MID, or null when the remote advertised none.</param>
/// <param name="Msid">The remote audio m-line's a=msid identity, or null when the remote advertised none.</param>
internal sealed record RemoteAudioTrackInfo(string? Mid, SdpMsid? Msid);
