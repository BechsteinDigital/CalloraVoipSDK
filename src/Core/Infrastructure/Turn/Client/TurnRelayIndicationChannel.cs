using System.Net;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// The permission-based TURN relay data path (RFC 8656 §10): it frames a payload as a <em>Send indication</em>
/// addressed to a specific peer, and recovers the peer and payload from an inbound <em>Data indication</em>.
/// <para>
/// Unlike <see cref="TurnRelayChannel"/> — which is bound to a single peer via ChannelBind and uses the
/// compact ChannelData framing — this addresses <em>any</em> permitted peer per datagram (the peer travels
/// in each indication's XOR-PEER-ADDRESS). That is what the ICE checking phase needs: connectivity checks
/// are sent to several remote candidates over the one allocation before any pair is nominated, so no single
/// channel can be bound yet. Installing a channel for the nominated pair (the ChannelData fast path) is a
/// later optimisation on top of this.
/// </para>
/// <para>
/// It performs no I/O — it only translates datagrams — and applies the same relay source filter as the
/// channel path (<see cref="RelayEndPoint"/>): a Data indication is accepted only when it arrives from the
/// relay server's exact 5-tuple.
/// </para>
/// </summary>
internal sealed class TurnRelayIndicationChannel
{
    private readonly IStunMessageCodec _codec;
    private readonly IPEndPoint _relayServer;

    /// <summary>Creates the indication relay bound to <paramref name="relayServer"/>.</summary>
    /// <param name="codec">The STUN wire codec used to encode/decode indications.</param>
    /// <param name="relayServer">The TURN server's 5-tuple that relayed traffic flows through.</param>
    public TurnRelayIndicationChannel(IStunMessageCodec codec, IPEndPoint relayServer)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(relayServer);
        _codec = codec;
        _relayServer = relayServer;
    }

    /// <summary>The relay server's endpoint; framed datagrams from <see cref="Wrap"/> are sent here.</summary>
    public IPEndPoint RelayServer => _relayServer;

    /// <summary>
    /// Frames <paramref name="payload"/> as a Send indication addressed to <paramref name="peer"/>. The
    /// returned datagram is addressed to <see cref="RelayServer"/>, which forwards the payload to the peer
    /// (provided a permission for the peer exists, RFC 8656 §9).
    /// </summary>
    /// <param name="peer">The peer the relay should forward the payload to.</param>
    /// <param name="payload">The media/transport bytes to relay.</param>
    /// <returns>The Send-indication datagram to send to the relay server.</returns>
    public byte[] Wrap(IPEndPoint peer, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(peer);

        var transactionId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(transactionId);

        var indication = new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Send,
            TransactionId = transactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peer }, transactionId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = payload.ToArray() }),
            ],
        };

        return _codec.Encode(indication);
    }

    /// <summary>
    /// Recovers the relayed peer and payload from an inbound datagram. Succeeds only when the datagram came
    /// from <see cref="RelayServer"/> and is a Data indication carrying XOR-PEER-ADDRESS and DATA.
    /// </summary>
    /// <param name="datagram">The raw inbound datagram from the socket.</param>
    /// <param name="source">The datagram's source endpoint (must be the relay server, dual-stack aware).</param>
    /// <param name="peer">The peer the payload was relayed from, or <see langword="null"/> when this returns false.</param>
    /// <param name="payload">The recovered inner payload, or an empty array when this returns false.</param>
    /// <returns><see langword="true"/> when the datagram is a Data indication relayed for us.</returns>
    public bool TryUnwrap(ReadOnlySpan<byte> datagram, IPEndPoint source, out IPEndPoint? peer, out byte[] payload)
    {
        peer = null;
        payload = Array.Empty<byte>();

        ArgumentNullException.ThrowIfNull(source);
        if (!RelayEndPoint.SameEndPoint(source, _relayServer))
            return false;

        StunMessage? decoded;
        try
        {
            decoded = _codec.Decode(datagram.ToArray());
        }
        catch (Exception)
        {
            return false;
        }

        if (decoded is null
            || decoded.MessageClass != StunMessageClass.Indication
            || (TurnMessageMethod)(ushort)decoded.MessageMethod != TurnMessageMethod.Data)
        {
            return false;
        }

        var peerEndPoint = TurnAttributeMapper.DecodeXorPeerAddress(decoded)?.EndPoint;
        var dataAttribute = TurnAttributeMapper.DecodeData(decoded);
        if (peerEndPoint is null || dataAttribute is null)
            return false;

        peer = peerEndPoint;
        payload = dataAttribute.Value.ToArray();
        return true;
    }
}
