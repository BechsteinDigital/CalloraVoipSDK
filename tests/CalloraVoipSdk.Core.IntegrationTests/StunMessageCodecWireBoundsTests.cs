using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Wire-bounds gate for <see cref="StunMessageCodec"/> against malformed attacker input
/// (HARD-A3/A4/A5). A truncated XOR-MAPPED-ADDRESS must not throw out of the decoder; the
/// integrity verifier must treat the 16-bit declared length — not the raw buffer size — as the
/// authoritative message boundary; and a zero-length-attribute flood must not mint unbounded
/// attribute objects.
/// </summary>
public sealed class StunMessageCodecWireBoundsTests
{
    private const uint MagicCookie = 0x2112A442;

    private static void WriteHeader(byte[] msg, int declaredLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0), 0x0101);                 // Binding success response
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)declaredLength); // STUN message length
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
        for (byte i = 0; i < 12; i++) msg[8 + i] = (byte)(i + 1);                     // transaction id
    }

    private static byte[] BuildSingleAttributeMessage(ushort attrType, byte[] attrValue)
    {
        var aligned = (attrValue.Length + 3) & ~3;
        var msg = new byte[20 + 4 + aligned];
        WriteHeader(msg, 4 + aligned);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), (ushort)attrValue.Length);
        attrValue.CopyTo(msg.AsSpan(24));
        return msg;
    }

    private static Core.Infrastructure.Stun.Messages.StunMessage? Decode(byte[] message)
        => new StunMessageCodec().Decode(message);

    // ── HARD-A3: address-attribute slice guards ───────────────────────────────

    [Fact]
    public void Decode_truncated_ipv6_xor_mapped_address_does_not_throw()
    {
        // family=0x02 (IPv6) but only 8 value bytes present (needs 20): the decoder must fall back
        // to an UnknownRawAttribute instead of slicing value[4..20] out of bounds.
        var value = new byte[] { 0x00, 0x02, 0x12, 0x34, 0xAA, 0xBB, 0xCC, 0xDD };
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.XorMappedAddress, value);

        var attr = Assert.Single(Decode(message)!.Attributes);
        var unknown = Assert.IsType<UnknownRawAttribute>(attr);
        Assert.Equal((ushort)StunAttributeType.XorMappedAddress, unknown.RawAttributeType);
    }

    [Fact]
    public void Decode_truncated_ipv4_xor_mapped_address_does_not_throw()
    {
        // family=0x01 (IPv4) but only 4 value bytes present (needs 8).
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34 };
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.XorMappedAddress, value);

        Assert.IsType<UnknownRawAttribute>(Assert.Single(Decode(message)!.Attributes));
    }

    [Fact]
    public void Decode_valid_ipv4_xor_mapped_address_still_decodes()
    {
        // Happy path must survive the added length guards.
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34, 0xDE, 0xAD, 0xBE, 0xEF };
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.XorMappedAddress, value);

        Assert.IsType<XorMappedAddressAttribute>(Assert.Single(Decode(message)!.Attributes));
    }

    // ── HARD-A4: verifier honours the declared length, not the buffer size ─────

    // Builds a 44-byte buffer carrying a MESSAGE-INTEGRITY at offset 20 whose HMAC is *correctly*
    // computed (adjusted length 24) for the given key. declaredLength selects whether that MI falls
    // inside the declared message (24) or beyond it (0) — the bytes are byte-for-byte identical.
    private static byte[] BuildMessageWithIntegrityAt20(byte[] key, int declaredLength)
    {
        var msg = new byte[20 + 4 + 20];
        WriteHeader(msg, declaredLength);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), (ushort)StunAttributeType.MessageIntegrity);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), 20);

        const ushort adjustedLength = 24; // offset(20) - header(20) + attrHeader(4) + 20
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        hmac.AppendData(msg.AsSpan(0, 2));
        Span<byte> adjusted = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(adjusted, adjustedLength);
        hmac.AppendData(adjusted);
        hmac.AppendData(msg.AsSpan(4, 16)); // magic cookie + transaction id, up to the MI attribute
        hmac.GetHashAndReset().CopyTo(msg.AsSpan(24));
        return msg;
    }

    [Fact]
    public void VerifyIntegrity_accepts_message_integrity_within_declared_length()
    {
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var message = BuildMessageWithIntegrityAt20(key, declaredLength: 24); // MI is inside the message

        Assert.True(new StunMessageCodec().VerifyIntegrity(message, key));
    }

    [Fact]
    public void VerifyIntegrity_ignores_message_integrity_beyond_declared_length()
    {
        // Same bytes, same valid HMAC — but the header declares a zero-length message, so the MI sits
        // in trailing bytes outside the message. A verifier that walked the raw buffer would accept
        // this forgery; bounding to the declared length must reject it.
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var message = BuildMessageWithIntegrityAt20(key, declaredLength: 0);

        Assert.False(new StunMessageCodec().VerifyIntegrity(message, key));
    }

    // ── HARD-A5: attribute-flood cap ──────────────────────────────────────────

    [Fact]
    public void Decode_attribute_flood_is_capped()
    {
        // 200 well-formed zero-length attributes (4 bytes each). Without a cap the decoder would
        // mint 200 attribute objects; the flood guard bounds the count.
        const int floodCount = 200;
        var msg = new byte[20 + (floodCount * 4)];
        WriteHeader(msg, floodCount * 4);
        for (int i = 0; i < floodCount; i++)
        {
            int at = 20 + (i * 4);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), 0x7F00); // unassigned comprehension-optional
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), 0);  // zero-length value
        }

        var decoded = Decode(msg);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Attributes.Count < floodCount, "attribute flood was not capped");
        Assert.Equal(64, decoded.Attributes.Count);
    }
}
