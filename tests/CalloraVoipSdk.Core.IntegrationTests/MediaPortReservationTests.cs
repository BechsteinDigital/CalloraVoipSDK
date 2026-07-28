using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The atomic RTP/RTCP port-pair reservation that closes the "RTCP N+1 never held" race: the pair is bound
/// and held continuously until handed to the RtpSession and the RTCP monitor, with no release-and-rebind.
/// </summary>
public sealed class MediaPortReservationTests
{
    [Fact]
    public void Reserve_binds_a_consecutive_pair_and_holds_both_ports()
    {
        using var reservation = MediaPortReservation.Reserve(IPAddress.Loopback);

        Assert.Equal(reservation.RtpPort + 1, reservation.RtcpPort);
        // Both ports are held — a second bind on either fails (this is exactly the EADDRINUSE the fix prevents).
        Assert.Throws<SocketException>(() => new UdpClient(new IPEndPoint(IPAddress.Loopback, reservation.RtpPort)));
        Assert.Throws<SocketException>(() => new UdpClient(new IPEndPoint(IPAddress.Loopback, reservation.RtcpPort)));
    }

    [Fact]
    public void Take_transfers_ownership_so_dispose_does_not_close_the_taken_socket()
    {
        var reservation = MediaPortReservation.Reserve(IPAddress.Loopback);
        var rtpPort = reservation.RtpPort;
        var rtcpPort = reservation.RtcpPort;

        using var rtp = reservation.TakeRtpSocket(); // taken → must survive the reservation's Dispose
        reservation.Dispose();                       // closes only the untaken RTCP socket

        // The taken RTP socket still owns its port.
        Assert.Throws<SocketException>(() => new UdpClient(new IPEndPoint(IPAddress.Loopback, rtpPort)));
        // The untaken RTCP port was released by Dispose and can be bound again.
        using var rebindRtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, rtcpPort));
        Assert.Equal(rtcpPort, ((IPEndPoint)rebindRtcp.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public void Take_is_callable_once_per_socket()
    {
        using var reservation = MediaPortReservation.Reserve(IPAddress.Loopback);

        reservation.TakeRtpSocket().Dispose();
        Assert.Throws<InvalidOperationException>(() => reservation.TakeRtpSocket());
    }

    [Fact]
    public void Reserve_still_finds_a_free_pair_under_port_contention()
    {
        // Occupy a spread of ports (forcing N+1 collisions and the retry path), then a fresh reservation
        // still returns a valid consecutive pair.
        var occupied = new List<UdpClient>();
        try
        {
            for (var i = 0; i < 200; i++)
                occupied.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));

            using var reservation = MediaPortReservation.Reserve(IPAddress.Loopback);
            Assert.Equal(reservation.RtpPort + 1, reservation.RtcpPort);
        }
        finally
        {
            foreach (var socket in occupied)
                socket.Dispose();
        }
    }
}
