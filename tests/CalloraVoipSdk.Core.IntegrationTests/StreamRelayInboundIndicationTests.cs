using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The inbound half of the relay ICE candidate over the stream transport (ADR-073 slice 4a, #240): during the
/// ICE checking phase a relayed Data indication (RFC 8656 §10) arriving on the stream is unwrapped to its inner
/// payload and the peer it came from — a connectivity-check response — while a non-Data STUN frame from the
/// server stays on the control path. The receive counterpart of slice 3's send path.
/// </summary>
public sealed class StreamRelayInboundIndicationTests
{
    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("203.0.113.7"), 3478);
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("198.51.100.30"), 50000);

    [Fact]
    public async Task A_relayed_data_indication_is_unwrapped_to_its_inner_payload_and_peer()
    {
        var (transport, serverStream, cleanup) = await StreamTransportAsync(onRelayControl: _ => { });
        try
        {
            var got = new TaskCompletionSource<(IPEndPoint Peer, byte[] Inner)>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.SetIndicationRelay(
                new TurnRelayIndicationChannel(new StunMessageCodec(), RelayServer),
                (peer, inner) => got.TrySetResult((peer, inner)));
            transport.Start();

            var inner = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            var indication = DataIndication(Peer, inner);
            await serverStream.WriteAsync(indication);
            await serverStream.FlushAsync();

            var (peer, payload) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Peer, peer);            // attributed to the relayed peer, not the TURN server
            Assert.Equal(inner, payload);        // the inner payload, not the Data-indication envelope
        }
        finally
        {
            await transport.DisposeAsync();
            cleanup();
        }
    }

    [Fact]
    public async Task A_non_data_stun_frame_from_the_server_stays_on_the_control_path()
    {
        var control = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (transport, serverStream, cleanup) = await StreamTransportAsync(onRelayControl: m => control.TrySetResult(m));
        try
        {
            var indicationDelivered = false;
            transport.SetIndicationRelay(
                new TurnRelayIndicationChannel(new StunMessageCodec(), RelayServer),
                (_, _) => indicationDelivered = true);
            transport.Start();

            var binding = StunBinding();
            await serverStream.WriteAsync(binding);
            await serverStream.FlushAsync();

            var onControl = await control.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(binding, onControl);          // the frame reached the control path verbatim
            Assert.False(indicationDelivered, "a non-Data STUN frame must not be delivered as a relayed indication");
        }
        finally
        {
            await transport.DisposeAsync();
            cleanup();
        }
    }

    // A stream transport wired over a loopback socket pair; the returned server stream is the "TURN server" side
    // the test writes framed messages into. The control callback is captured via the out list.
    private static async Task<(StreamRelayMediaTransport Transport, NetworkStream ServerStream, Action Cleanup)> StreamTransportAsync(
        Action<byte[]> onRelayControl)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var server = await accept;

        var transport = new StreamRelayMediaTransport(
            client.GetStream(), RelayServer, onRelayControl, _ => { }, NullLogger<StreamRelayMediaTransport>.Instance);

        return (transport, server.GetStream(), () =>
        {
            server.Dispose();
            client.Dispose();
            listener.Stop();
        });
    }

    private static byte[] StunBinding()
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return new StunMessageCodec().Encode(new StunMessage
        {
            MessageClass = StunMessageClass.Request,
            MessageMethod = StunMessageMethod.Binding,
            TransactionId = transactionId,
            Attributes = [],
        });
    }

    private static byte[] DataIndication(IPEndPoint peer, byte[] innerPayload)
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return new StunMessageCodec().Encode(new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = transactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peer }, transactionId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = innerPayload }),
            ],
        });
    }
}
