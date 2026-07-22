namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Shared defaults for media (RTP/RTCP/DTLS/STUN) UDP sockets. Keeps the two independent receive-buffer
/// concerns — the kernel socket queue and the user-space per-datagram buffer — deliberately separate, so
/// the one is never sized by the other (the conflation that undersized SO_RCVBUF at 8 KiB).
/// </summary>
internal static class MediaSocketDefaults
{
    /// <summary>
    /// Requested kernel receive buffer (SO_RCVBUF) for a media socket, in bytes. This queues many pending
    /// datagrams the receive loop has not read yet, so it must absorb the short processing pauses (GC, SRTP
    /// of a large video frame) that otherwise cause kernel drops once video bitrates (BWE ceiling ~5 Mbps)
    /// are in play — where the previous 8 KiB was far too small. The OS clamps the request to its own maximum
    /// (for example Linux <c>net.core.rmem_max</c>), so this is an upper request, not a guarantee.
    /// </summary>
    public const int SocketReceiveBufferBytes = 1024 * 1024;

    /// <summary>
    /// User-space buffer size for a single datagram receive (the pooled buffer one <c>ReceiveFrom</c> writes
    /// into), in bytes. Sized to the largest media datagram we accept — MTU-bounded RTP/RTCP with headroom —
    /// and independent of <see cref="SocketReceiveBufferBytes"/> above, which queues many such datagrams.
    /// </summary>
    public const int DatagramBufferBytes = 8192;
}
