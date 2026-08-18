using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream relay media transport (ADR-073 slice 1, #240): the transport-agnostic
/// <see cref="TurnRelayCoordinator"/> drives Allocate → CreatePermission → ChannelBind over a persistent TCP
/// stream to a real hosted <see cref="TurnServerHost"/>, and once bound the transport frames media as
/// ChannelData over the stream padded to a 4-byte boundary (RFC 8656 §12.5) — the write side the framer's read
/// side already expects.
/// </summary>
public sealed class StreamRelayMediaTransportTests
{
    private const ushort ChannelNumber = 0x4001;

    [Fact]
    public async Task The_coordinator_completes_the_turn_handshake_over_the_stream_transport()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tcp,
            RequireAuthentication = false,
        });
        host.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.LocalEndPoint);
        var stream = tcp.GetStream();

        TurnRelayCoordinator coordinator = null!;
        await using var transport = new StreamRelayMediaTransport(
            stream, host.LocalEndPoint,
            onRelayControl: m => coordinator.OnControlDatagram(m),
            onInboundMedia: _ => { },
            NullLogger<StreamRelayMediaTransport>.Instance);
        coordinator = new TurnRelayCoordinator(
            transport, host.LocalEndPoint, new StunMessageCodec(), NullLogger<TurnRelayCoordinator>.Instance);
        transport.Start();

        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 50000);
        var allocation = await coordinator
            .EstablishAsync(peer, ChannelNumber, credentials: null, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The whole control sequence ran over the stream: a relayed address was granted and the bound channel
        // installed, moving the transport into its data phase.
        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        Assert.Equal(ChannelNumber, Assert.IsType<TurnRelayChannel>(allocation.Channel).ChannelNumber);
        Assert.True(allocation.LifetimeSeconds > 0, "the allocation must grant a positive lifetime");
    }

    [Theory]
    [InlineData(0)]   // 4+0 = 4, already aligned
    [InlineData(1)]   // 4+1 = 5 → 3 pad
    [InlineData(2)]   // 4+2 = 6 → 2 pad
    [InlineData(3)]   // 4+3 = 7 → 1 pad
    [InlineData(4)]   // 4+4 = 8, aligned
    public async Task Media_is_framed_as_channel_data_padded_to_a_four_byte_boundary(int payloadLength)
    {
        // A one-directional stream the transport writes into; we then read the framed bytes back with the very
        // reader the receive path uses, so write and read padding must agree.
        using var wire = new MemoryStream();
        var payload = new byte[payloadLength];
        for (var i = 0; i < payloadLength; i++)
            payload[i] = (byte)(0xA0 + i);

        await using (var transport = new StreamRelayMediaTransport(
            wire, RelayServer, _ => { }, _ => { }, NullLogger<StreamRelayMediaTransport>.Instance))
        {
            transport.SetRelayChannel(new TurnRelayChannel(RelayServer, ChannelNumber));
            await transport.SendMediaAsync(payload, CancellationToken.None);
        }

        var written = wire.ToArray();
        Assert.Equal(0, written.Length % 4);   // §12.5: the whole frame is 4-byte aligned on the wire

        using var readback = new MemoryStream(written);
        var frame = await TurnStreamFramer.ReadFrameAsync(readback);
        Assert.NotNull(frame);
        Assert.True(frame!.IsChannelData);
        Assert.Equal(ChannelNumber, frame.ChannelNumber);
        Assert.Equal(payload, frame.Payload);
        Assert.Equal(written.Length, readback.Position);   // the reader consumed exactly the frame + its padding
    }

    [Fact]
    public async Task Media_is_suppressed_until_a_channel_is_installed()
    {
        using var wire = new MemoryStream();
        await using var transport = new StreamRelayMediaTransport(
            wire, RelayServer, _ => { }, _ => { }, NullLogger<StreamRelayMediaTransport>.Instance);

        await transport.SendMediaAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Empty(wire.ToArray());   // nothing framed before the channel is bound
    }

    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("203.0.113.7"), 3478);
}
