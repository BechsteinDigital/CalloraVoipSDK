using System.Net;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behaviour of <see cref="TurnRelayIndicationChannel"/> — the permission-based relay data path that frames
/// payloads as Send indications for an arbitrary peer and recovers (peer, payload) from Data indications
/// (RFC 8656 §10). This is what the ICE checking phase uses before any channel is bound.
/// </summary>
public sealed class TurnRelayIndicationChannelTests
{
    private static readonly IPEndPoint RelayServer = new(IPAddress.Parse("192.0.2.1"), 3478);
    private static readonly IPEndPoint PeerA = new(IPAddress.Parse("203.0.113.7"), 50000);
    private static readonly IPEndPoint PeerB = new(IPAddress.Parse("203.0.113.8"), 50001);

    [Fact]
    public void Wrap_frames_the_payload_as_a_send_indication_addressed_to_the_peer()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);

        var framed = relay.Wrap(PeerA, new byte[] { 1, 2, 3 });

        var decoded = codec.Decode(framed)!;
        Assert.Equal(StunMessageClass.Indication, decoded.MessageClass);
        Assert.Equal(TurnMessageMethod.Send, (TurnMessageMethod)(ushort)decoded.MessageMethod);
        Assert.Equal(PeerA, TurnAttributeMapper.DecodeXorPeerAddress(decoded)?.EndPoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, TurnAttributeMapper.DecodeData(decoded)?.Value.ToArray());
    }

    [Fact]
    public void Wrap_addresses_different_peers_independently()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);

        var toA = codec.Decode(relay.Wrap(PeerA, new byte[] { 9 }))!;
        var toB = codec.Decode(relay.Wrap(PeerB, new byte[] { 9 }))!;

        Assert.Equal(PeerA, TurnAttributeMapper.DecodeXorPeerAddress(toA)?.EndPoint);
        Assert.Equal(PeerB, TurnAttributeMapper.DecodeXorPeerAddress(toB)?.EndPoint);
    }

    [Fact]
    public void TryUnwrap_recovers_the_peer_and_payload_from_a_data_indication()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);

        var dataIndication = DataIndication(codec, PeerB, new byte[] { 4, 5, 6 });

        Assert.True(relay.TryUnwrap(dataIndication, RelayServer, out var peer, out var payload));
        Assert.Equal(PeerB, peer);
        Assert.Equal(new byte[] { 4, 5, 6 }, payload);
    }

    [Fact]
    public void TryUnwrap_rejects_a_datagram_from_a_non_relay_source()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);
        var dataIndication = DataIndication(codec, PeerB, new byte[] { 4, 5, 6 });

        var offPath = new IPEndPoint(IPAddress.Parse("198.51.100.66"), 3478);
        Assert.False(relay.TryUnwrap(dataIndication, offPath, out _, out _));
    }

    [Fact]
    public void TryUnwrap_rejects_a_non_data_indication()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);

        // A Send indication (our own outbound shape) is not an inbound Data indication.
        var sendIndication = relay.Wrap(PeerA, new byte[] { 1 });
        Assert.False(relay.TryUnwrap(sendIndication, RelayServer, out _, out _));

        // Random non-STUN bytes.
        Assert.False(relay.TryUnwrap(new byte[] { 0x01, 0x02, 0x03 }, RelayServer, out _, out _));
    }

    [Fact]
    public void TryUnwrap_rejects_a_data_indication_missing_the_data_attribute()
    {
        var codec = new StunMessageCodec();
        var relay = new TurnRelayIndicationChannel(codec, RelayServer);

        var txId = NewTransactionId();
        var noData = codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = txId,
            Attributes = [TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = PeerB }, txId)],
        });

        Assert.False(relay.TryUnwrap(noData, RelayServer, out _, out _));
    }

    private static byte[] DataIndication(IStunMessageCodec codec, IPEndPoint peer, byte[] payload)
    {
        var txId = NewTransactionId();
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = txId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peer }, txId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = payload }),
            ],
        });
    }

    private static byte[] NewTransactionId()
    {
        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }
}
