using System.Net;
using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Reserves a consecutive RTP/RTCP UDP port pair (N, N+1) atomically and holds both bound sockets, so no
/// other call can claim N+1 between SDP publication and the RTCP monitor start. The sockets are handed to
/// the <c>RtpSession</c> and the RTCP monitor via <see cref="TakeRtpSocket"/>/<see cref="TakeRtcpSocket"/>
/// — there is no release-and-rebind, which is what removes the port-ownership race: a port change after
/// the SDP is published would be too late, since the SDP already advertised N+1 as the RTCP port
/// (RFC 3550 §11, <c>a=rtcp</c>).
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
    /// Reserves a consecutive (N, N+1) pair on <paramref name="bindAddress"/>: binds RTP on an OS-assigned
    /// port N, then N+1; if N+1 is already owned it discards N and retries with a fresh N, up to
    /// <paramref name="maxAttempts"/>. Both sockets are bound and held on return.
    /// </summary>
    /// <exception cref="IOException">No consecutive pair could be reserved within the attempt budget.</exception>
    public static MediaPortReservation Reserve(IPAddress bindAddress, int maxAttempts = 32)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var rtp = new UdpClient(new IPEndPoint(bindAddress, 0));
            var rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;

            // N+1 must be a representable UDP port; ushort.MaxValue leaves no room for the RTCP port.
            if (rtpPort >= ushort.MaxValue)
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
            $"Could not reserve a consecutive RTP/RTCP UDP port pair on {bindAddress} within {maxAttempts} attempts.");
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
