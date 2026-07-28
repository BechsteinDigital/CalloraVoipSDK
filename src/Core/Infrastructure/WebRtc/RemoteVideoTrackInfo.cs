using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// One remote video m-line the peer will send to us (P2c: N tracks) — its MID (<c>a=mid</c>, RFC 5888) and
/// its MediaStream/track identity (<c>a=msid</c>, RFC 8830). The receiver materialises one public
/// <see cref="WebRtc.RemoteTrack"/> per entry, so several remote cameras/screen-shares stay separable.
/// </summary>
/// <param name="Mid">The remote video m-line's MID, or null when the remote advertised none.</param>
/// <param name="Msid">The remote video m-line's a=msid identity, or null when the remote advertised none.</param>
internal sealed record RemoteVideoTrackInfo(string? Mid, SdpMsid? Msid);
