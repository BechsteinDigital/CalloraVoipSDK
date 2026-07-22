using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for issue #12: media sockets pinned SO_RCVBUF to 8 KiB, far too small for video bitrates
/// (BWE ceiling ~5 Mbps) — short processing pauses caused kernel drops. The kernel receive buffer must
/// default large and stay separate from the user-space per-datagram buffer (the two were conflated on
/// one 8 KiB constant). The OS clamps the request to its own maximum, so the behavioural check asserts a
/// value far above the old 8 KiB rather than the exact request.
/// </summary>
public sealed class MediaSocketReceiveBufferTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSessionOptions Options(int localPort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    [Fact]
    public void Default_kernel_receive_buffer_is_large_enough_for_video()
    {
        // ≥ 256 KiB absorbs the processing pauses that dropped video at 8 KiB (a small multiple of the
        // BWE ceiling's per-100 ms byte budget).
        Assert.True(
            MediaSocketDefaults.SocketReceiveBufferBytes >= 256 * 1024,
            $"SO_RCVBUF default {MediaSocketDefaults.SocketReceiveBufferBytes} B is too small for video.");
    }

    [Fact]
    public void Kernel_receive_buffer_is_separate_from_the_datagram_buffer()
    {
        // The two concerns must not be sized by the same value again: the kernel queue holds many
        // datagrams, the user-space buffer holds one (MTU-bounded).
        Assert.NotEqual(MediaSocketDefaults.DatagramBufferBytes, MediaSocketDefaults.SocketReceiveBufferBytes);
        Assert.Equal(8192, MediaSocketDefaults.DatagramBufferBytes);
    }

    [Fact]
    public void RtpSessionOptions_defaults_the_socket_receive_buffer_to_the_shared_default()
    {
        var options = Options(FreeUdpPort());

        Assert.Equal(MediaSocketDefaults.SocketReceiveBufferBytes, options.SocketReceiveBufferBytes);
    }

    [Fact]
    public async Task RtpSession_socket_gets_a_kernel_buffer_far_larger_than_the_old_8_KiB()
    {
        await using var session = new RtpSession(
            Options(FreeUdpPort()), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        // The OS clamps the 1 MiB request to net.core.rmem_max (typically ~208 KiB, reported doubled),
        // so assert well above the old 8 KiB default rather than the exact request. With the old code the
        // granted value was ~8–16 KiB and this would fail.
        Assert.True(
            session.EffectiveSocketReceiveBufferBytes >= 64 * 1024,
            $"Effective SO_RCVBUF {session.EffectiveSocketReceiveBufferBytes} B is not larger than the old 8 KiB.");
    }
}
