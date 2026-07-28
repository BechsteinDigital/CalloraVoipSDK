using System.Net;
using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Reserves a consecutive RTP/RTCP UDP port pair (even N for RTP, odd N+1 for RTCP — RFC 3550 §11)
/// atomically and holds both bound sockets, so no other call can claim N+1 between SDP publication and
/// the RTCP monitor start. The bound sockets are handed straight to the <c>RtpSession</c> and the RTCP
/// monitor via <see cref="TakeRtpSocket"/>/<see cref="TakeRtcpSocket"/> — the reserved socket <em>is</em>
/// the media socket, there is no release-and-rebind. That is what removes the port-ownership race: N+1 is
/// never released, so nothing can grab it, and a port change after the SDP advertised N+1 would be too
/// late anyway. This mirrors pjsip/SIPSorcery/baresip: wildcard bind, even RTP port, both-or-none retry.
/// </summary>
internal sealed class MediaPortReservation : IDisposable
{
    private UdpClient? _rtp;
    private UdpClient? _rtcp;
    private bool _disposed;

    private MediaPortReservation(UdpClient rtp, UdpClient rtcp)
    {
        _rtp = rtp;
        _rtcp = rtcp;
        RtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
    }

    /// <summary>The reserved RTP port N.</summary>
    public int RtpPort { get; }

    /// <summary>The reserved RTCP port N+1.</summary>
    public int RtcpPort => RtpPort + 1;

    /// <summary>
    /// Reserves a consecutive (even N, odd N+1) pair on <paramref name="bindAddress"/>: binds RTP on an
    /// OS-assigned port and, when that port is even and N+1 is free, holds both; otherwise it discards and
    /// retries, up to <paramref name="maxAttempts"/>. Both sockets are bound and held on return. Bind on
    /// <see cref="IPAddress.Any"/> for the reference-parity wildcard behaviour (the SDP advertises the
    /// route-local address separately).
    /// </summary>
    /// <exception cref="IOException">No consecutive even/odd pair could be reserved within the attempt budget.</exception>
    public static MediaPortReservation Reserve(IPAddress bindAddress, int maxAttempts = 64)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var rtp = new UdpClient(new IPEndPoint(bindAddress, 0));
            var rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;

            // RFC 3550 §11: RTP on an even port, RTCP on the following odd port. An odd OS-assigned port
            // cannot host the pair (and the last port leaves no room for N+1) — discard and retry.
            if ((rtpPort & 1) != 0 || rtpPort >= ushort.MaxValue)
            {
                rtp.Dispose();
                continue;
            }

            try
            {
                var rtcp = new UdpClient(new IPEndPoint(bindAddress, rtpPort + 1));
                return new MediaPortReservation(rtp, rtcp);
            }
            catch (SocketException)
            {
                // N+1 is owned by another socket (this call's own earlier port, another call, another
                // process). Discard N and retry with a fresh OS-assigned port.
                rtp.Dispose();
            }
        }

        throw new IOException(
            $"Could not reserve a consecutive even/odd RTP/RTCP UDP port pair on {bindAddress} within {maxAttempts} attempts.");
    }

    /// <summary>
    /// Transfers ownership of the RTP socket to the caller; the reservation no longer closes it, so the
    /// caller (the RtpSession) must dispose it. Callable once.
    /// </summary>
    public UdpClient TakeRtpSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = _rtp ?? throw new InvalidOperationException("The RTP socket has already been taken.");
        _rtp = null;
        return socket;
    }

    /// <summary>
    /// Transfers ownership of the RTCP socket to the caller; the reservation no longer closes it, so the
    /// caller (the RTCP monitor) must dispose it. Callable once.
    /// </summary>
    public UdpClient TakeRtcpSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = _rtcp ?? throw new InvalidOperationException("The RTCP socket has already been taken.");
        _rtcp = null;
        return socket;
    }

    /// <summary>Closes any socket that was not taken over. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rtp?.Dispose();
        _rtcp?.Dispose();
    }
}
