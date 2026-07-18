namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// The RTP header-extension URIs (RFC 8285) the SDK knows, negotiated via SDP <c>a=extmap</c>.
/// Currently the transport-wide sequence number used by congestion control (transport-cc /
/// RFC 8888): the sender stamps a transport-wide 16-bit counter, the receiver reports arrival
/// times keyed by it.
/// </summary>
internal static class RtpHeaderExtensionUris
{
    /// <summary>
    /// Transport-wide congestion control sequence number
    /// (draft-holmer-rmcat-transport-wide-cc-extensions-01). This is the URI Chrome/libwebrtc has
    /// long used and matches the vast majority of WebRTC endpoints; the resolver matches it exactly
    /// (Ordinal). FOLLOW-UP: accept additional/registered URIs (e.g. an RFC 8888 URN) once the BWE
    /// chain is wired end-to-end.
    /// </summary>
    internal const string TransportWideCc =
        "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01";

    /// <summary>
    /// Media identification (MID) SDES header extension (RFC 9143 / RFC 8843 §15). Carries the m-line's
    /// <c>a=mid</c> token per packet so a BUNDLE receiver can route an inbound packet to the right media
    /// section before an SSRC is latched. This is the exact URN browsers/libwebrtc negotiate via
    /// <c>a=extmap</c>; the resolver matches it Ordinal.
    /// </summary>
    internal const string Mid = "urn:ietf:params:rtp-hdrext:sdes:mid";

    /// <summary>
    /// RTP stream identification (RID) SDES header extension (RFC 8852). Carries an encoding's
    /// <c>a=rid</c> id per packet so a receiver can associate a simulcast stream's SSRC with its
    /// <c>a=rid</c> encoding (RFC 8851 / RFC 8853) before the SSRC is otherwise known. This is the exact
    /// URN browsers/libwebrtc negotiate via <c>a=extmap</c>; the resolver matches it Ordinal.
    /// </summary>
    internal const string Rid = "urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id";
}
