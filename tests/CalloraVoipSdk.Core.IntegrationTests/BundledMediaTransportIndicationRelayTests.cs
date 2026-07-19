using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The per-pair TURN relay indication inbound routing (Slice 4d-3b-2a): on a direct-mode transport with an
/// indication relay set, datagrams from the relay server are the relayed control/data plane — a Data
/// indication is unwrapped to its inner payload and the peer it was relayed from (RFC 8656 §10), a non-Data
/// STUN control response goes to the control callback, and neither reaches the media pipeline as direct peer
/// traffic — while a genuine direct (host/srflx) peer datagram still passes through with its own source, so
/// the direct and relay ICE candidates coexist. A real relay-server socket drives each case over loopback.
/// </summary>
public sealed class BundledMediaTransportIndicationRelayTests
{
    [Fact]
    public async Task A_relayed_data_indication_surfaces_the_inner_payload_with_the_peer_as_source()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;
        var peer = new IPEndPoint(IPAddress.Loopback, 40404); // the remote candidate the check was relayed from

        var stun = StunTcs();
        var pipeline = Pipeline();
        pipeline.StunPacketReceived += (data, src) => stun.TrySetResult((data, src));

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            pipeline, NullLogger<BundledMediaTransport>.Instance);
        transport.SetIndicationRelay(new TurnRelayIndicationChannel(new StunMessageCodec(), relayEndPoint));
        await transport.StartAsync();

        var inner = StunBindingRequest();
        var indication = DataIndication(peer, inner);
        await relayServer.SendAsync(indication, indication.Length, transport.LocalEndPoint);

        var (received, source) = await stun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(inner, received);   // unwrapped inner payload, not the Data-indication envelope
        Assert.Equal(peer, source);      // attributed to the relayed peer, not the TURN server
    }

    [Fact]
    public async Task A_control_response_from_the_relay_server_goes_to_the_control_callback()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;

        var control = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stun = StunTcs();
        var pipeline = Pipeline();
        pipeline.StunPacketReceived += (data, src) => stun.TrySetResult((data, src));

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            pipeline, NullLogger<BundledMediaTransport>.Instance);
        transport.SetIndicationRelay(
            new TurnRelayIndicationChannel(new StunMessageCodec(), relayEndPoint),
            onControl: bytes => control.TrySetResult(bytes.ToArray()));
        await transport.StartAsync();

        // A plain STUN message from the relay server that is not a Data indication — a stand-in for a
        // CreatePermission/Refresh success response.
        var response = StunBindingRequest();
        await relayServer.SendAsync(response, response.Length, transport.LocalEndPoint);

        var forwarded = await control.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(response, forwarded);
        Assert.False(stun.Task.IsCompleted); // relay-server control must not reach the media pipeline
    }

    [Fact]
    public async Task A_direct_peer_datagram_passes_through_with_its_own_source()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;
        using var directPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var directPeerEndPoint = (IPEndPoint)directPeer.Client.LocalEndPoint!;

        var stun = StunTcs();
        var pipeline = Pipeline();
        pipeline.StunPacketReceived += (data, src) => stun.TrySetResult((data, src));

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            pipeline, NullLogger<BundledMediaTransport>.Instance);
        transport.SetIndicationRelay(new TurnRelayIndicationChannel(new StunMessageCodec(), relayEndPoint));
        await transport.StartAsync();

        // A host/srflx candidate's check arrives directly from the peer, not through the relay server: it must
        // pass through with the peer as source so the direct ICE candidate keeps working alongside the relay.
        var check = StunBindingRequest();
        await directPeer.SendAsync(check, check.Length, transport.LocalEndPoint);

        var (received, source) = await stun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(check, received);
        Assert.Equal(directPeerEndPoint, source);
    }

    [Fact]
    public async Task A_non_control_datagram_from_the_relay_server_is_dropped_not_forwarded()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;

        var control = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stun = StunTcs();
        var pipeline = Pipeline();
        pipeline.StunPacketReceived += (data, src) => stun.TrySetResult((data, src));

        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = Loopback() },
            pipeline, NullLogger<BundledMediaTransport>.Instance);
        transport.SetIndicationRelay(
            new TurnRelayIndicationChannel(new StunMessageCodec(), relayEndPoint),
            onControl: bytes => control.TrySetResult(bytes.ToArray()));
        await transport.StartAsync();

        // A TURN ChannelData frame (first byte 0x40) from the relay server: not a Data indication and not
        // STUN-classified. It must be dropped — never reaching the pipeline nor the control callback.
        var channelData = new byte[] { 0x40, 0x01, 0x00, 0x02, 0xAA, 0xBB };
        await relayServer.SendAsync(channelData, channelData.Length, transport.LocalEndPoint);

        // Round-trip a Data indication afterwards to prove the loop kept running and the earlier frame was
        // silently dropped rather than forwarded.
        var peer = new IPEndPoint(IPAddress.Loopback, 40405);
        var inner = StunBindingRequest();
        var indication = DataIndication(peer, inner);
        await relayServer.SendAsync(indication, indication.Length, transport.LocalEndPoint);

        var (received, source) = await stun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(inner, received);
        Assert.Equal(peer, source);
        Assert.False(control.Task.IsCompleted); // the non-control frame was not forwarded to the callback
    }

    [Fact]
    public async Task SetIndicationRelay_is_rejected_on_a_whole_socket_relay_transport()
    {
        using var relayServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndPoint = (IPEndPoint)relayServer.Client.LocalEndPoint!;

        // A whole-socket relay-mode transport (RelayServer set) is mutually exclusive with the per-pair
        // indication path: enabling the indication relay on it must be rejected.
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions
            {
                LocalEndPoint = Loopback(),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9999),
                RelayServer = relayEndPoint,
            },
            Pipeline(), NullLogger<BundledMediaTransport>.Instance);

        Assert.Throws<InvalidOperationException>(() =>
            transport.SetIndicationRelay(new TurnRelayIndicationChannel(new StunMessageCodec(), relayEndPoint)));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static TaskCompletionSource<(byte[] Data, IPEndPoint Source)> StunTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 0);

    private static BundledInboundPipeline Pipeline()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            3, new Dictionary<string, IReadOnlyCollection<int>>());
        var router = new BundledTrackRouter(demux);
        return new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
    }

    // A minimal 20-byte STUN Binding request (RFC 5389 header + magic cookie) — enough for
    // MediaPacketClassifier to recognise it as STUN and route it to StunPacketReceived.
    private static byte[] StunBindingRequest()
    {
        var message = new byte[20];
        message[0] = 0x00;
        message[1] = 0x01;
        message[4] = 0x21;
        message[5] = 0x12;
        message[6] = 0xA4;
        message[7] = 0x42;
        RandomNumberGenerator.Fill(message.AsSpan(8, 12));
        return message;
    }

    // Builds a TURN Data indication (RFC 8656 §10) carrying XOR-PEER-ADDRESS and DATA, as the relay server
    // sends when forwarding a peer's datagram back to the client.
    private static byte[] DataIndication(IPEndPoint peer, byte[] innerPayload)
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);

        var indication = new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = transactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peer }, transactionId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = innerPayload }),
            ],
        };

        return new StunMessageCodec().Encode(indication);
    }
}
