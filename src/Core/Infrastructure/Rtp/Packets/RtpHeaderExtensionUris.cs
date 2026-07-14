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
}
