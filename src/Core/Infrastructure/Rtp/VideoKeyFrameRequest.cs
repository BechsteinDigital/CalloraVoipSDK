namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Which of our outbound video streams a peer asked a key frame for (RFC 4585 §6.3.1 PLI, RFC 5104 §4.3.1 FIR):
/// the media SSRC the request named, and the simulcast layer that SSRC belongs to when the m-line is
/// simulcasting (RFC 8853).
/// </summary>
/// <remarks>
/// The attribution is what a forwarding layer needs. An SFU receiving a downstream PLI has to know which
/// upstream source it corresponds to; without it the only safe move is to ask <em>every</em> sender for a key
/// frame, which turns one receiver's decoder reset into a bandwidth spike from all of them — and it happens
/// exactly when the link is already struggling, because that is what made the receiver ask.
/// </remarks>
/// <param name="MediaSsrc">The media SSRC the PLI or FIR named — one of this track's sending SSRCs.</param>
/// <param name="Rid">
/// The <c>a=rid</c> layer that SSRC sends, or <see langword="null"/> for a stream that is not simulcasting.
/// </param>
internal readonly record struct VideoKeyFrameRequest(uint MediaSsrc, string? Rid);
