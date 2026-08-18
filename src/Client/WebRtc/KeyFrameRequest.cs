namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Which outbound video stream the remote peer asked a key frame for, surfaced on
/// <see cref="IPeerConnection.VideoTrackKeyFrameRequested"/> for each inbound PLI (RFC 4585 §6.3.1) or FIR
/// (RFC 5104 §4.3.1).
/// </summary>
/// <remarks>
/// The attribution is what a forwarding layer needs. Given only "someone wants a key frame", a server relaying
/// several participants has no safe move but to ask <em>every</em> sender for one — so in a room of three or
/// more, one receiver's decoder reset produces a key frame from all of them, at the moment the link is already
/// struggling, because that is what made the receiver ask. With the stream named, it asks that source alone.
/// </remarks>
/// <param name="Mid">The media identification (<c>a=mid</c>) of the track the request is about.</param>
/// <param name="MediaSsrc">
/// The media SSRC the PLI or FIR named. Zero when the peer sent no usable SSRC — unattributed, which is
/// deliberately distinct from attributed to the wrong stream.
/// </param>
/// <param name="Rid">
/// The simulcast layer (<c>a=rid</c>, RFC 8853) that SSRC carries, or <see langword="null"/> when the track is
/// not simulcasting or the SSRC could not be resolved to a layer.
/// </param>
public readonly record struct KeyFrameRequest(string Mid, uint MediaSsrc, string? Rid);
