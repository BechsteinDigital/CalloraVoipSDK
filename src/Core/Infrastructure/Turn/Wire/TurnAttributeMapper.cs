using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

/// <summary>
/// Maps TURN attribute models to STUN <see cref="UnknownRawAttribute"/> payloads and back.
/// <para>
/// TURN uses the STUN wire format, but TURN attribute codes are intentionally kept inside
/// the TURN module so protocol logic stays isolated from the STUN module.
/// </para>
/// </summary>
internal static class TurnAttributeMapper
{
    /// <summary>Encodes REQUESTED-TRANSPORT into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnRequestedTransportAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.RequestedTransport)
        {
            Value = new byte[] { (byte)attribute.Protocol, 0x00, 0x00, 0x00 }
        };
    }

    /// <summary>Encodes REQUESTED-ADDRESS-FAMILY into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnRequestedAddressFamilyAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.RequestedAddressFamily)
        {
            Value = new byte[] { (byte)attribute.Family, 0x00, 0x00, 0x00 }
        };
    }

    /// <summary>Encodes LIFETIME into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnLifetimeAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var value = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, attribute.Seconds);

        return new UnknownRawAttribute((ushort)TurnAttributeType.Lifetime)
        {
            Value = value
        };
    }

    /// <summary>Encodes EVEN-PORT into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnEvenPortAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.EvenPort)
        {
            Value = new byte[] { attribute.ReserveNextPort ? (byte)0x80 : (byte)0x00 }
        };
    }

    /// <summary>Encodes DONT-FRAGMENT into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnDontFragmentAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.DontFragment)
        {
            Value = Array.Empty<byte>()
        };
    }

    /// <summary>Encodes RESERVATION-TOKEN into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnReservationTokenAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var value = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(value, attribute.Token);

        return new UnknownRawAttribute((ushort)TurnAttributeType.ReservationToken)
        {
            Value = value
        };
    }

    /// <summary>Encodes RFC 6062 CONNECTION-ID into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnConnectionIdAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var value = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, attribute.ConnectionId);

        return new UnknownRawAttribute((ushort)TurnAttributeType.ConnectionId)
        {
            Value = value
        };
    }

    /// <summary>Encodes XOR-PEER-ADDRESS into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnXorPeerAddressAttribute attribute, ReadOnlySpan<byte> transactionId)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.XorPeerAddress)
        {
            Value = TurnWireAddressCodec.EncodeXorAddressValue(attribute.EndPoint, transactionId)
        };
    }

    /// <summary>Encodes XOR-RELAYED-ADDRESS into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnXorRelayedAddressAttribute attribute, ReadOnlySpan<byte> transactionId)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.XorRelayedAddress)
        {
            Value = TurnWireAddressCodec.EncodeXorAddressValue(attribute.EndPoint, transactionId)
        };
    }

    /// <summary>Encodes CHANNEL-NUMBER into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnChannelNumberAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var value = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(value, attribute.ChannelNumber);
        // value[2..4] are RFFU and stay zero.

        return new UnknownRawAttribute((ushort)TurnAttributeType.ChannelNumber)
        {
            Value = value
        };
    }

    /// <summary>Encodes DATA into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnDataAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.Data)
        {
            Value = attribute.Value.ToArray()
        };
    }

    /// <summary>Encodes RFC 8016 MOBILITY-TICKET into a raw TURN attribute.</summary>
    public static UnknownRawAttribute Encode(TurnMobilityTicketAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return new UnknownRawAttribute((ushort)TurnAttributeType.MobilityTicket)
        {
            Value = attribute.Ticket.ToArray()
        };
    }

    /// <summary>Decodes LIFETIME from a STUN/TURN message.</summary>
    public static TurnLifetimeAttribute? DecodeLifetime(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.Lifetime);
        if (raw is null || raw.Value.Length < 4)
            return null;

        return new TurnLifetimeAttribute
        {
            Seconds = BinaryPrimitives.ReadUInt32BigEndian(raw.Value.Span)
        };
    }

    /// <summary>Decodes XOR-RELAYED-ADDRESS from a STUN/TURN message.</summary>
    public static TurnXorRelayedAddressAttribute? DecodeXorRelayedAddress(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.XorRelayedAddress);
        if (raw is null)
            return null;

        return TurnWireAddressCodec.TryDecodeXorAddressValue(raw.Value.Span, message.TransactionId, out var endPoint)
            ? new TurnXorRelayedAddressAttribute { EndPoint = endPoint! }
            : null;
    }

    /// <summary>Decodes XOR-PEER-ADDRESS from a STUN/TURN message.</summary>
    public static TurnXorPeerAddressAttribute? DecodeXorPeerAddress(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.XorPeerAddress);
        if (raw is null)
            return null;

        return TurnWireAddressCodec.TryDecodeXorAddressValue(raw.Value.Span, message.TransactionId, out var endPoint)
            ? new TurnXorPeerAddressAttribute { EndPoint = endPoint! }
            : null;
    }

    /// <summary>Decodes REQUESTED-TRANSPORT from a STUN/TURN message.</summary>
    public static TurnRequestedTransportAttribute? DecodeRequestedTransport(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.RequestedTransport);
        if (raw is null || raw.Value.Length < 1)
            return null;

        return new TurnRequestedTransportAttribute
        {
            Protocol = (TurnRequestedTransportProtocol)raw.Value.Span[0]
        };
    }

    /// <summary>Decodes REQUESTED-ADDRESS-FAMILY from a STUN/TURN message.</summary>
    public static TurnRequestedAddressFamilyAttribute? DecodeRequestedAddressFamily(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.RequestedAddressFamily);
        if (raw is null || raw.Value.Length < 1)
            return null;

        return new TurnRequestedAddressFamilyAttribute
        {
            Family = (TurnAddressFamily)raw.Value.Span[0]
        };
    }

    /// <summary>Decodes RESERVATION-TOKEN from a STUN/TURN message.</summary>
    public static TurnReservationTokenAttribute? DecodeReservationToken(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.ReservationToken);
        if (raw is null || raw.Value.Length < 8)
            return null;

        return new TurnReservationTokenAttribute
        {
            Token = BinaryPrimitives.ReadUInt64BigEndian(raw.Value.Span)
        };
    }

    /// <summary>Decodes EVEN-PORT from a STUN/TURN message (RFC 8656 §14.6): the high bit is the reserve flag.</summary>
    public static TurnEvenPortAttribute? DecodeEvenPort(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.EvenPort);
        if (raw is null || raw.Value.Length < 1)
            return null;

        return new TurnEvenPortAttribute
        {
            ReserveNextPort = (raw.Value.Span[0] & 0x80) != 0
        };
    }

    /// <summary>Decodes CHANNEL-NUMBER from a STUN/TURN message.</summary>
    public static TurnChannelNumberAttribute? DecodeChannelNumber(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.ChannelNumber);
        if (raw is null || raw.Value.Length < 2)
            return null;

        return new TurnChannelNumberAttribute
        {
            ChannelNumber = BinaryPrimitives.ReadUInt16BigEndian(raw.Value.Span)
        };
    }

    /// <summary>Decodes DATA from a STUN/TURN message.</summary>
    public static TurnDataAttribute? DecodeData(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.Data);
        if (raw is null)
            return null;

        return new TurnDataAttribute
        {
            Value = raw.Value.ToArray()
        };
    }

    /// <summary>Decodes RFC 6062 CONNECTION-ID from a STUN/TURN message.</summary>
    public static TurnConnectionIdAttribute? DecodeConnectionId(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.ConnectionId);
        if (raw is null || raw.Value.Length < 4)
            return null;

        return new TurnConnectionIdAttribute
        {
            ConnectionId = BinaryPrimitives.ReadUInt32BigEndian(raw.Value.Span)
        };
    }

    /// <summary>Decodes RFC 8016 MOBILITY-TICKET from a STUN/TURN message.</summary>
    public static TurnMobilityTicketAttribute? DecodeMobilityTicket(StunMessage message)
    {
        var raw = FindRaw(message, TurnAttributeType.MobilityTicket);
        if (raw is null)
            return null;

        return new TurnMobilityTicketAttribute
        {
            Ticket = raw.Value.ToArray()
        };
    }

    private static UnknownRawAttribute? FindRaw(StunMessage message, TurnAttributeType type)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Attributes
            .OfType<UnknownRawAttribute>()
            .FirstOrDefault(attribute => attribute.RawAttributeType == (ushort)type);
    }
}
