using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

/// <summary>
/// Encodes and decodes STUN messages per RFC 5389 and ICE extensions (RFC 8445).
/// <para>
/// Type word layout (14 active bits, bits 15–14 always zero):
/// M11-M7 at bits 13–9 | C1 at bit 8 | M6-M4 at bits 7–5 | C0 at bit 4 | M3-M0 at bits 3–0.
/// Encoding: typeWord = ((method &amp; 0x0F80) &lt;&lt; 2) | (class &amp; 0x0110) | ((method &amp; 0x0070) &lt;&lt; 1) | (method &amp; 0x000F)
/// Decoding: method = ((tw &gt;&gt; 2) &amp; 0x0F80) | ((tw &gt;&gt; 1) &amp; 0x0070) | (tw &amp; 0x000F);  class = tw &amp; 0x0110
/// </para>
/// <para>
/// MESSAGE-INTEGRITY: HMAC-SHA1 over bytes [0..offset_before_MI] with the length field
/// adjusted to include the MESSAGE-INTEGRITY attribute (24 bytes) before hashing.
/// </para>
/// <para>
/// FINGERPRINT: CRC32 (IEEE 802.3) XOR 0x5354554E over bytes [0..offset_before_FP]
/// with the length field adjusted to include the FINGERPRINT attribute (8 bytes).
/// </para>
/// </summary>
internal sealed class StunMessageCodec : IStunMessageCodec
{
    // Upper bound on attributes decoded from one message. Well above any real STUN/TURN message
    // (typically < 20) yet far below the ~16k a zero-length-attribute flood could otherwise force.
    private const int MaxAttributesPerMessage = 64;


    /// <inheritdoc />
    public byte[] Encode(StunMessage message)
        => EncodeCore(message, ReadOnlySpan<byte>.Empty, addFingerprint: false);

    /// <inheritdoc />
    public byte[] EncodeWithIntegrity(
        StunMessage        message,
        ReadOnlySpan<byte> hmacKey,
        bool               addFingerprint = true)
        => EncodeCore(message, hmacKey, addFingerprint);

    /// <inheritdoc />
    public StunMessage? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < StunWireConstants.HeaderSize)
            return null;

        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != StunWireConstants.MagicCookie)
            return null;

        ushort typeWord    = BinaryPrimitives.ReadUInt16BigEndian(data);
        ushort declaredLen = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);

        if (data.Length < StunWireConstants.HeaderSize + declaredLen)
            return null;

        var txId = data[8..20].ToArray();

        var msgClass = (StunMessageClass)(typeWord & 0x0110);
        var msgMethod = (StunMessageMethod)(
            ((typeWord >> 2) & 0x0F80) |
            ((typeWord >> 1) & 0x0070) |
            (typeWord        & 0x000F));

        var attrSpan   = data[StunWireConstants.HeaderSize..(StunWireConstants.HeaderSize + declaredLen)];
        var attributes = DecodeAttributes(attrSpan, txId);

        return new StunMessage
        {
            MessageClass  = msgClass,
            MessageMethod = msgMethod,
            TransactionId = txId,
            Attributes    = attributes
        };
    }

    /// <inheritdoc />
    public bool IsStunPacket(ReadOnlySpan<byte> data)
        => data.Length >= StunWireConstants.HeaderSize
           && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == StunWireConstants.MagicCookie;

    /// <inheritdoc />
    public bool VerifyIntegrity(ReadOnlySpan<byte> rawMessage, ReadOnlySpan<byte> hmacKey)
    {
        if (rawMessage.Length < StunWireConstants.HeaderSize)
            return false;

        int messageEnd = MessageEndFromDeclaredLength(rawMessage);
        if (messageEnd < 0)
            return false;

        int offset = StunWireConstants.HeaderSize;
        while (offset + StunWireConstants.AttributeHeaderSize <= messageEnd)
        {
            ushort type      = BinaryPrimitives.ReadUInt16BigEndian(rawMessage[offset..]);
            ushort attrLen   = BinaryPrimitives.ReadUInt16BigEndian(rawMessage[(offset + 2)..]);
            int    dataStart = offset + StunWireConstants.AttributeHeaderSize;

            if (dataStart + attrLen > messageEnd)
                break;

            if (type == (ushort)StunAttributeType.MessageIntegrity)
            {
                if (attrLen != 20)
                    return false;

                // Bounded by the declared length above, so offset - HeaderSize + 24 <= 65535: the
                // ushort length field (RFC 5389 §15.4) can never overflow.
                var adjustedLength = (ushort)(
                    offset
                    - StunWireConstants.HeaderSize
                    + StunWireConstants.AttributeHeaderSize
                    + 20);

                var computed = ComputeHmacSha1WithAdjustedLength(rawMessage, offset, adjustedLength, hmacKey);
                var stored = rawMessage[dataStart..(dataStart + 20)];
                return CryptographicOperations.FixedTimeEquals(stored, computed);
            }

            offset = dataStart + AlignTo4(attrLen);
        }

        return false; // MESSAGE-INTEGRITY not found within the declared message.
    }

    /// <summary>
    /// Returns the exclusive end offset of the message as fixed by the 16-bit STUN length field,
    /// or -1 when the buffer does not actually contain the declared number of bytes. Binding parsers
    /// to this — rather than the raw buffer length — ignores trailing bytes of an oversized receive
    /// buffer and keeps the adjusted-length arithmetic inside the 16-bit field.
    /// </summary>
    private static int MessageEndFromDeclaredLength(ReadOnlySpan<byte> rawMessage)
    {
        ushort declaredLength = BinaryPrimitives.ReadUInt16BigEndian(rawMessage[2..]);
        int messageEnd = StunWireConstants.HeaderSize + declaredLength;
        return messageEnd > rawMessage.Length ? -1 : messageEnd;
    }

    /// <inheritdoc />
    public bool VerifyIntegrity(byte[] rawMessage, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        ArgumentNullException.ThrowIfNull(hmacKey);
        return VerifyIntegrity(rawMessage.AsSpan(), hmacKey.AsSpan());
    }

    /// <inheritdoc />
    public bool VerifyFingerprint(ReadOnlySpan<byte> rawMessage)
    {
        if (rawMessage.Length < StunWireConstants.HeaderSize)
            return false;

        int messageEnd = MessageEndFromDeclaredLength(rawMessage);
        if (messageEnd < 0)
            return false;

        int offset = StunWireConstants.HeaderSize;
        while (offset + StunWireConstants.AttributeHeaderSize <= messageEnd)
        {
            ushort type      = BinaryPrimitives.ReadUInt16BigEndian(rawMessage[offset..]);
            ushort attrLen   = BinaryPrimitives.ReadUInt16BigEndian(rawMessage[(offset + 2)..]);
            int    dataStart = offset + StunWireConstants.AttributeHeaderSize;

            if (dataStart + attrLen > messageEnd)
                break;

            if (type == (ushort)StunAttributeType.Fingerprint)
            {
                if (attrLen != 4)
                    return false;

                uint stored = BinaryPrimitives.ReadUInt32BigEndian(rawMessage[dataStart..]);

                // Bounded by the declared length above, so the adjusted length stays inside 16 bits.
                var adjustedLength = (ushort)(
                    offset
                    - StunWireConstants.HeaderSize
                    + StunWireConstants.AttributeHeaderSize
                    + 4);

                uint crc = ComputeCrc32WithAdjustedLength(rawMessage, offset, adjustedLength);
                uint expected = crc ^ StunWireConstants.FingerprintXorMask;
                return stored == expected;
            }

            offset = dataStart + AlignTo4(attrLen);
        }

        return false; // FINGERPRINT not found.
    }

    /// <inheritdoc />
    public bool VerifyFingerprint(byte[] rawMessage)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        return VerifyFingerprint(rawMessage.AsSpan());
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>Core encoding path shared by all public encode methods.</summary>
    private static byte[] EncodeCore(
        StunMessage        message,
        ReadOnlySpan<byte> hmacKey,
        bool               addFingerprint)
    {
        // Collect all user-supplied attributes; skip computed special attributes.
        var segments    = new List<byte[]>();
        int userAttrLen = 0;
        foreach (var attr in message.Attributes)
        {
            if (attr.AttributeType is StunAttributeType.MessageIntegrity
                                   or StunAttributeType.Fingerprint)
                continue;

            var seg = EncodeAttribute(attr, message.TransactionId);
            segments.Add(seg);
            userAttrLen += seg.Length;
        }

        bool addIntegrity = !hmacKey.IsEmpty;
        int  totalLen     = StunWireConstants.HeaderSize
                          + userAttrLen
                          + (addIntegrity   ? StunWireConstants.AttributeHeaderSize + 20 : 0)
                          + (addFingerprint ? StunWireConstants.AttributeHeaderSize + 4  : 0);

        var buffer = new byte[totalLen];
        var span   = buffer.AsSpan();

        // Encode message type word.
        ushort method   = (ushort)message.MessageMethod;
        ushort msgClass = (ushort)message.MessageClass;
        ushort typeWord = (ushort)(
            ((method & 0x0F80) << 2) |
            (msgClass & 0x0110)      |
            ((method & 0x0070) << 1) |
            (method & 0x000F));

        BinaryPrimitives.WriteUInt16BigEndian(span,      typeWord);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(totalLen - StunWireConstants.HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], StunWireConstants.MagicCookie);
        message.TransactionId.CopyTo(span[8..]);

        int offset = StunWireConstants.HeaderSize;
        foreach (var seg in segments)
        {
            seg.CopyTo(span[offset..]);
            offset += seg.Length;
        }

        if (addIntegrity)
            offset = AppendMessageIntegrity(buffer, span, offset, userAttrLen, hmacKey, addFingerprint);

        if (addFingerprint)
            AppendFingerprint(buffer, span, offset);

        return buffer;
    }

    /// <summary>
    /// Appends MESSAGE-INTEGRITY, temporarily adjusting the length field per RFC 5389 §15.4.
    /// Returns the new offset after the attribute.
    /// </summary>
    private static int AppendMessageIntegrity(
        byte[]             buffer,
        Span<byte>         span,
        int                offset,
        int                userAttrLen,
        ReadOnlySpan<byte> hmacKey,
        bool               fingerprintFollows)
    {
        int integrityAttrSize = StunWireConstants.AttributeHeaderSize + 20;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(userAttrLen + integrityAttrSize));

        var hmacData = ComputeHmacSha1(buffer.AsSpan(0, offset), hmacKey);

        BinaryPrimitives.WriteUInt16BigEndian(span[offset..],       (ushort)StunAttributeType.MessageIntegrity);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], 20);
        hmacData.CopyTo(span[(offset + 4)..]);
        offset += integrityAttrSize;

        int finalAttrLen = offset - StunWireConstants.HeaderSize
                         + (fingerprintFollows ? StunWireConstants.AttributeHeaderSize + 4 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)finalAttrLen);
        return offset;
    }

    /// <summary>Appends FINGERPRINT, adjusting the length field per RFC 5389 §15.5.</summary>
    private static void AppendFingerprint(byte[] buffer, Span<byte> span, int offset)
    {
        int fingerprintAttrSize   = StunWireConstants.AttributeHeaderSize + 4;
        int preFingerprintAttrLen = offset - StunWireConstants.HeaderSize;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(preFingerprintAttrLen + fingerprintAttrSize));

        uint crc         = ComputeCrc32(buffer.AsSpan(0, offset));
        uint fingerprint = crc ^ StunWireConstants.FingerprintXorMask;

        BinaryPrimitives.WriteUInt16BigEndian(span[offset..],       (ushort)StunAttributeType.Fingerprint);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], fingerprint);
    }

    // ── Attribute encoding ────────────────────────────────────────────────────

    /// <summary>Dispatches to the appropriate attribute encoder.</summary>
    private static byte[] EncodeAttribute(StunAttribute attribute, byte[] transactionId)
    {
        return attribute switch
        {
            XorMappedAddressAttribute xma => EncodeMappedAddress(
                (ushort)StunAttributeType.XorMappedAddress, xma.EndPoint, transactionId, xor: true),

            MappedAddressAttribute ma => EncodeMappedAddress(
                (ushort)StunAttributeType.MappedAddress, ma.EndPoint, transactionId, xor: false),

            AlternateServerAttribute alt => EncodeMappedAddress(
                (ushort)StunAttributeType.AlternateServer, alt.EndPoint, transactionId, xor: false),

            UsernameAttribute ua  => EncodeStringAttribute((ushort)StunAttributeType.Username, ua.Value),
            RealmAttribute    ra  => EncodeStringAttribute((ushort)StunAttributeType.Realm,    ra.Value),
            NonceAttribute    na  => EncodeStringAttribute((ushort)StunAttributeType.Nonce,    na.Value),
            ThirdPartyAuthorizationAttribute tpa => EncodeStringAttribute(
                (ushort)StunAttributeType.ThirdPartyAuthorization,
                tpa.ServerName),
            SoftwareAttribute sa  => EncodeStringAttribute((ushort)StunAttributeType.Software, sa.Description),
            AccessTokenAttribute at => EncodePaddedAttribute(
                (ushort)StunAttributeType.AccessToken,
                at.Token.Span),

            ErrorCodeAttribute ec => EncodeErrorCode(ec),

            UnknownAttributesAttribute ua => EncodeUnknownAttributes(ua),

            ChangeRequestAttribute cr => EncodeUInt32Attribute(
                (ushort)StunAttributeType.ChangeRequest,
                (uint)((cr.ChangeIp ? 0x04 : 0) | (cr.ChangePort ? 0x02 : 0))),

            PriorityAttribute pa => EncodeUInt32Attribute((ushort)StunAttributeType.Priority, pa.Value),

            UseCandidateAttribute => EncodePaddedAttribute((ushort)StunAttributeType.UseCandidate, []),

            IceControlledAttribute ica  => EncodeUInt64Attribute((ushort)StunAttributeType.IceControlled,  ica.TieBreaker),
            IceControllingAttribute ica => EncodeUInt64Attribute((ushort)StunAttributeType.IceControlling, ica.TieBreaker),

            UnknownRawAttribute raw => EncodePaddedAttribute(raw.RawAttributeType, raw.Value.Span),

            _ => throw new ArgumentException($"Cannot encode STUN attribute of type {attribute.AttributeType}.")
        };
    }

    /// <summary>Encodes MAPPED-ADDRESS, XOR-MAPPED-ADDRESS, or ALTERNATE-SERVER (RFC 5389 §15.1–15.2).</summary>
    private static byte[] EncodeMappedAddress(
        ushort     typeCode,
        IPEndPoint endPoint,
        byte[]     transactionId,
        bool       xor)
    {
        bool isIpv6  = endPoint.AddressFamily == AddressFamily.InterNetworkV6;
        int  addrLen = isIpv6 ? 16 : 4;
        int  valLen  = 4 + addrLen;

        var buf  = new byte[StunWireConstants.AttributeHeaderSize + AlignTo4(valLen)];
        var span = buf.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span,      typeCode);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)valLen);
        span[4] = 0x00;
        span[5] = (byte)(isIpv6 ? 0x02 : 0x01);

        ushort port = (ushort)endPoint.Port;
        if (xor) port ^= StunWireConstants.MagicCookieHighWord;
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], port);

        var addrBytes = endPoint.Address.GetAddressBytes();
        if (xor)
        {
            Span<byte> magic = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(magic, StunWireConstants.MagicCookie);
            for (int i = 0; i < 4; i++) addrBytes[i] ^= magic[i];
            if (isIpv6)
                for (int i = 0; i < 12; i++) addrBytes[4 + i] ^= transactionId[i];
        }

        addrBytes.CopyTo(span[8..]);
        return buf;
    }

    /// <summary>Encodes a UTF-8 string attribute with 4-byte padding.</summary>
    private static byte[] EncodeStringAttribute(ushort typeCode, string value)
        => EncodePaddedAttribute(typeCode, Encoding.UTF8.GetBytes(value));

    /// <summary>Encodes an ERROR-CODE attribute (RFC 5389 §15.6).</summary>
    private static byte[] EncodeErrorCode(ErrorCodeAttribute error)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(error.Reason);
        int valLen      = 4 + reasonBytes.Length;
        var buf         = new byte[StunWireConstants.AttributeHeaderSize + AlignTo4(valLen)];
        var span        = buf.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span,      (ushort)StunAttributeType.ErrorCode);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)valLen);
        span[4] = 0;
        span[5] = 0;
        span[6] = (byte)(error.Code / 100);
        span[7] = (byte)(error.Code % 100);
        reasonBytes.CopyTo(span[8..]);
        return buf;
    }

    /// <summary>Encodes UNKNOWN-ATTRIBUTES as a list of 2-byte type codes (RFC 5389 §15.9).</summary>
    private static byte[] EncodeUnknownAttributes(UnknownAttributesAttribute attr)
    {
        int valLen = attr.UnknownTypeCodes.Count * 2;
        var buf    = new byte[StunWireConstants.AttributeHeaderSize + AlignTo4(valLen)];
        var span   = buf.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span,      (ushort)StunAttributeType.UnknownAttributes);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)valLen);

        for (int i = 0; i < attr.UnknownTypeCodes.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(span[(4 + i * 2)..], attr.UnknownTypeCodes[i]);

        return buf;
    }

    /// <summary>Encodes a 4-byte unsigned integer attribute.</summary>
    private static byte[] EncodeUInt32Attribute(ushort typeCode, uint value)
    {
        var buf = new byte[StunWireConstants.AttributeHeaderSize + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(),      typeCode);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2),     4);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4),     value);
        return buf;
    }

    /// <summary>Encodes an 8-byte unsigned integer attribute (ICE tiebreaker).</summary>
    private static byte[] EncodeUInt64Attribute(ushort typeCode, ulong value)
    {
        var buf = new byte[StunWireConstants.AttributeHeaderSize + 8];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(),  typeCode);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(4), value);
        return buf;
    }

    /// <summary>Encodes a generic attribute with 4-byte-aligned value padding.</summary>
    private static byte[] EncodePaddedAttribute(ushort typeCode, ReadOnlySpan<byte> value)
    {
        var buf  = new byte[StunWireConstants.AttributeHeaderSize + AlignTo4(value.Length)];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span,      typeCode);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)value.Length);
        value.CopyTo(span[4..]);
        return buf;
    }

    // ── Attribute decoding ────────────────────────────────────────────────────

    /// <summary>Decodes all attributes from the attribute section of a STUN message.</summary>
    private static IReadOnlyList<StunAttribute> DecodeAttributes(
        ReadOnlySpan<byte> attrData,
        byte[]             transactionId)
    {
        var result = new List<StunAttribute>();
        int offset = 0;

        while (offset + StunWireConstants.AttributeHeaderSize <= attrData.Length)
        {
            // Attribute-flood guard: a pathological message of zero-length attributes advances the
            // offset only 4 bytes per attribute, so a 65535-byte section could mint ~16k attribute
            // objects. A real STUN/TURN message carries far fewer; stop once the cap is reached.
            if (result.Count >= MaxAttributesPerMessage)
                break;

            ushort attrType  = BinaryPrimitives.ReadUInt16BigEndian(attrData[offset..]);
            ushort attrLen   = BinaryPrimitives.ReadUInt16BigEndian(attrData[(offset + 2)..]);
            int    dataStart = offset + StunWireConstants.AttributeHeaderSize;

            if (dataStart + attrLen > attrData.Length)
                break; // Truncated attribute — stop gracefully.

            var value = attrData[dataStart..(dataStart + attrLen)];
            result.Add(DecodeAttribute(attrType, value, transactionId));

            offset = dataStart + AlignTo4(attrLen);
        }

        return result;
    }

    /// <summary>Decodes a single STUN attribute by dispatching on type code.</summary>
    private static StunAttribute DecodeAttribute(
        ushort             typeCode,
        ReadOnlySpan<byte> value,
        byte[]             transactionId)
    {
        return (StunAttributeType)typeCode switch
        {
            StunAttributeType.ChangeRequest when value.Length >= 4 => new ChangeRequestAttribute
            {
                ChangeIp   = (BinaryPrimitives.ReadUInt32BigEndian(value) & 0x04) != 0,
                ChangePort = (BinaryPrimitives.ReadUInt32BigEndian(value) & 0x02) != 0,
            },

            StunAttributeType.MappedAddress    => DecodeMappedAddress(typeCode, value, transactionId, xor: false),
            StunAttributeType.XorMappedAddress => DecodeMappedAddress(typeCode, value, transactionId, xor: true),
            StunAttributeType.AlternateServer  => DecodeAlternateServer(value),

            StunAttributeType.Username => new UsernameAttribute { Value = Encoding.UTF8.GetString(value) },
            StunAttributeType.Realm    => new RealmAttribute    { Value = Encoding.UTF8.GetString(value) },
            StunAttributeType.Nonce    => new NonceAttribute    { Value = Encoding.UTF8.GetString(value) },
            StunAttributeType.ThirdPartyAuthorization => new ThirdPartyAuthorizationAttribute
            {
                ServerName = Encoding.UTF8.GetString(value)
            },
            StunAttributeType.Software => new SoftwareAttribute { Description = Encoding.UTF8.GetString(value) },
            StunAttributeType.AccessToken => new AccessTokenAttribute { Token = value.ToArray() },

            StunAttributeType.ErrorCode => DecodeErrorCode(value),

            StunAttributeType.UnknownAttributes => DecodeUnknownAttributes(value),

            StunAttributeType.MessageIntegrity => new MessageIntegrityAttribute
            {
                Hmac = value.ToArray()
            },
            StunAttributeType.Fingerprint when value.Length >= 4 => new FingerprintAttribute
            {
                Value = BinaryPrimitives.ReadUInt32BigEndian(value)
            },

            StunAttributeType.Priority when value.Length >= 4 => new PriorityAttribute
            {
                Value = BinaryPrimitives.ReadUInt32BigEndian(value)
            },

            StunAttributeType.UseCandidate => new UseCandidateAttribute(),

            StunAttributeType.IceControlled when value.Length >= 8 => new IceControlledAttribute
            {
                TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(value)
            },
            StunAttributeType.IceControlling when value.Length >= 8 => new IceControllingAttribute
            {
                TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(value)
            },

            _ => new UnknownRawAttribute(typeCode) { Value = value.ToArray() }
        };
    }

    /// <summary>Decodes a MAPPED-ADDRESS or XOR-MAPPED-ADDRESS attribute.</summary>
    private static StunAttribute DecodeMappedAddress(
        ushort             typeCode,
        ReadOnlySpan<byte> value,
        byte[]             transactionId,
        bool               xor)
    {
        if (value.Length < 4)
            return new UnknownRawAttribute(typeCode) { Value = value.ToArray() };

        byte   family = value[1];
        ushort port   = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        if (xor) port ^= StunWireConstants.MagicCookieHighWord;

        IPAddress address;
        if (family == 0x01) // IPv4: 4-byte address at value[4..8]
        {
            if (value.Length < 8)
                return new UnknownRawAttribute(typeCode) { Value = value.ToArray() };

            var addrBytes = value[4..8].ToArray();
            if (xor)
            {
                Span<byte> magic = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(magic, StunWireConstants.MagicCookie);
                for (int i = 0; i < 4; i++) addrBytes[i] ^= magic[i];
            }
            address = new IPAddress(addrBytes);
        }
        else if (family == 0x02) // IPv6: 16-byte address at value[4..20]
        {
            // Guard the slice: a truncated IPv6 attribute (family=0x02 but < 20 bytes) must not
            // throw out of the decoder — a malformed attribute becomes an UnknownRawAttribute.
            if (value.Length < 20)
                return new UnknownRawAttribute(typeCode) { Value = value.ToArray() };

            var addrBytes = value[4..20].ToArray();
            if (xor)
            {
                Span<byte> magic = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(magic, StunWireConstants.MagicCookie);
                for (int i = 0; i < 4;  i++) addrBytes[i]     ^= magic[i];
                for (int i = 0; i < 12; i++) addrBytes[4 + i] ^= transactionId[i];
            }
            address = new IPAddress(addrBytes);
        }
        else // unknown address family: do not guess the layout
        {
            return new UnknownRawAttribute(typeCode) { Value = value.ToArray() };
        }

        var endPoint = new IPEndPoint(address, port);
        return xor
            ? new XorMappedAddressAttribute  { EndPoint = endPoint }
            : (StunAttribute)new MappedAddressAttribute { EndPoint = endPoint };
    }

    /// <summary>Decodes an ALTERNATE-SERVER attribute (same wire format as MAPPED-ADDRESS, non-XOR).</summary>
    private static StunAttribute DecodeAlternateServer(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4)
            return new UnknownRawAttribute((ushort)StunAttributeType.AlternateServer) { Value = value.ToArray() };

        byte   family = value[1];
        ushort port   = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);

        IPAddress address;
        if (family == 0x01)
        {
            address = new IPAddress(value[4..8].ToArray());
        }
        else
        {
            address = value.Length >= 20 ? new IPAddress(value[4..20].ToArray()) : IPAddress.Any;
        }

        return new AlternateServerAttribute { EndPoint = new IPEndPoint(address, port) };
    }

    /// <summary>Decodes an ERROR-CODE attribute (RFC 5389 §15.6).</summary>
    private static ErrorCodeAttribute DecodeErrorCode(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4)
            return new ErrorCodeAttribute { Code = 500, Reason = string.Empty };

        int    errorClass  = value[2] & 0x07;
        int    errorNumber = value[3];
        int    code        = errorClass * 100 + errorNumber;
        string reason      = value.Length > 4 ? Encoding.UTF8.GetString(value[4..]) : string.Empty;
        return new ErrorCodeAttribute { Code = code, Reason = reason };
    }

    /// <summary>Decodes UNKNOWN-ATTRIBUTES as a list of 2-byte type codes (RFC 5389 §15.9).</summary>
    private static UnknownAttributesAttribute DecodeUnknownAttributes(ReadOnlySpan<byte> value)
    {
        var codes = new List<ushort>(value.Length / 2);
        for (int i = 0; i + 1 < value.Length; i += 2)
            codes.Add(BinaryPrimitives.ReadUInt16BigEndian(value[i..]));

        return new UnknownAttributesAttribute { UnknownTypeCodes = codes };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Rounds a byte length up to the next multiple of 4 for STUN attribute padding.</summary>
    private static int AlignTo4(int length) => (length + 3) & ~3;

    private static byte[] ComputeHmacSha1(ReadOnlySpan<byte> data, ReadOnlySpan<byte> hmacKey)
    {
        var key = hmacKey.ToArray();
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        CryptographicOperations.ZeroMemory(key);
        hmac.AppendData(data);
        return hmac.GetHashAndReset();
    }

    private static byte[] ComputeHmacSha1WithAdjustedLength(
        ReadOnlySpan<byte> rawMessage,
        int offset,
        ushort adjustedLength,
        ReadOnlySpan<byte> hmacKey)
    {
        var key = hmacKey.ToArray();
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        CryptographicOperations.ZeroMemory(key);

        hmac.AppendData(rawMessage[..2]);

        Span<byte> adjustedLengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(adjustedLengthBytes, adjustedLength);
        hmac.AppendData(adjustedLengthBytes);

        hmac.AppendData(rawMessage[4..offset]);
        return hmac.GetHashAndReset();
    }

    private static uint ComputeCrc32WithAdjustedLength(
        ReadOnlySpan<byte> rawMessage,
        int offset,
        ushort adjustedLength)
    {
        uint crc = 0xFFFFFFFF;

        UpdateCrc32(ref crc, rawMessage[..2]);
        Span<byte> adjustedLengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(adjustedLengthBytes, adjustedLength);
        UpdateCrc32(ref crc, adjustedLengthBytes);
        UpdateCrc32(ref crc, rawMessage[4..offset]);

        return ~crc;
    }

    /// <summary>
    /// Computes an IEEE 802.3 CRC32 checksum over the given span.
    /// Used for FINGERPRINT computation (RFC 5389 §15.5).
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        UpdateCrc32(ref crc, data);
        return ~crc;
    }

    private static void UpdateCrc32(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
    }
}
