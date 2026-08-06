using System.Buffers.Binary;
using System.Net;
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

    // ── #156 P1-4: ALTERNATE-SERVER must use the SAME length/family gates as MAPPED-ADDRESS ──

    [Fact]
    public void Decode_truncated_ipv4_alternate_server_does_not_throw()
    {
        // family=0x01 (IPv4) but only 4 value bytes present (needs 8): the decoder must fall back to an
        // UnknownRawAttribute, not slice value[4..8] out of bounds and throw (#156 P1-4).
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34 };
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.AlternateServer, value);

        Assert.IsType<UnknownRawAttribute>(Assert.Single(Decode(message)!.Attributes));
    }

    [Fact]
    public void Decode_unknown_family_alternate_server_never_yields_a_wildcard_endpoint()
    {
        // An unknown address family must become an UnknownRawAttribute — never an ALTERNATE-SERVER
        // redirect to IPAddress.Any (0.0.0.0), which would silently point the client at the wildcard.
        var value = new byte[] { 0x00, 0x03, 0x12, 0x34, 0xAA, 0xBB, 0xCC, 0xDD };
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.AlternateServer, value);

        Assert.IsType<UnknownRawAttribute>(Assert.Single(Decode(message)!.Attributes));
    }

    [Fact]
    public void Decode_valid_ipv4_alternate_server_still_decodes()
    {
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34, 0xC0, 0xA8, 0x01, 0x01 }; // 192.168.1.1 : 0x1234
        var message = BuildSingleAttributeMessage((ushort)StunAttributeType.AlternateServer, value);

        var attr = Assert.IsType<AlternateServerAttribute>(Assert.Single(Decode(message)!.Attributes));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.1"), 0x1234), attr.EndPoint);
    }

    [Theory]
    [InlineData(0x01)] // IPv4
    [InlineData(0x02)] // IPv6
    [InlineData(0x03)] // unknown family — must never be interpreted as an address
    public void Decode_alternate_server_over_all_value_lengths_never_throws_or_wildcards(byte family)
    {
        // Table/fuzz over every value length 0..20: the decoder must never throw and never redirect to
        // the wildcard address (the typed/Try* contract for untrusted STUN wire input, #156 P1-4).
        for (var len = 0; len <= 20; len++)
        {
            // Non-zero bytes so a legitimately decoded address is never itself 0.0.0.0 — the assertion
            // below then only catches a wildcard *fallback*, not an all-zero address from the wire.
            var value = new byte[len];
            for (var i = 0; i < len; i++) value[i] = (byte)(i + 1);
            if (len >= 2) value[1] = family;
            var message = BuildSingleAttributeMessage((ushort)StunAttributeType.AlternateServer, value);

            var attr = Assert.Single(Decode(message)!.Attributes); // never throws
            if (family == 0x03)
                Assert.IsType<UnknownRawAttribute>(attr); // an unknown family is never guessed as an address
            else if (attr is AlternateServerAttribute alt)
                Assert.NotEqual(IPAddress.Any, alt.EndPoint.Address); // never a 0.0.0.0 redirect fallback
        }
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

    // ── HARD-A5 / #184: attribute-flood and malformed structure fail closed ────

    [Fact]
    public void Decode_attribute_flood_is_rejected()
    {
        // 200 well-formed zero-length attributes (4 bytes each) exceed the per-message attribute cap. A real
        // STUN/TURN message carries far fewer, so the whole message is rejected (fail closed, #184) rather
        // than truncated to the first 64 — a truncating parse could drop a trailing MESSAGE-INTEGRITY /
        // FINGERPRINT, an authentication bypass and amplification primitive.
        const int floodCount = 200;
        var msg = new byte[20 + (floodCount * 4)];
        WriteHeader(msg, floodCount * 4);
        for (int i = 0; i < floodCount; i++)
        {
            int at = 20 + (i * 4);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), 0x7F00); // unassigned comprehension-optional
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), 0);  // zero-length value
        }

        Assert.Null(Decode(msg));
    }

    [Fact]
    public void Decode_rejects_nonzero_top_two_bits()
    {
        // RFC 5389 §6: the two most-significant bits of every STUN message MUST be zero.
        var msg = BuildSingleAttributeMessage((ushort)StunAttributeType.Software, [1, 2, 3, 4]);
        msg[0] |= 0x80;

        Assert.Null(Decode(msg));
    }

    [Fact]
    public void Decode_rejects_a_declared_length_that_is_not_a_multiple_of_four()
    {
        // RFC 5389 §15: the message length is always a multiple of 4 (attributes are padded).
        var msg = BuildSingleAttributeMessage((ushort)StunAttributeType.Software, [1, 2, 3, 4]);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 10); // not a multiple of 4

        Assert.Null(Decode(msg));
    }

    [Fact]
    public void Decode_rejects_a_truncated_trailing_attribute()
    {
        // Header declares an 8-byte section: a zero-length attribute followed by a second attribute whose
        // header claims a 4-byte value that runs past the section. The truncated TLV must reject the whole
        // message (#184), not return the first attribute and silently drop the malformed remainder.
        var msg = new byte[20 + 8];
        WriteHeader(msg, 8);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), 0x7F00); // attr 1 type
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), 0);      // attr 1 length = 0
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(24), 0x7F01); // attr 2 type
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(26), 4);      // attr 2 length = 4, but no value remains

        Assert.Null(Decode(msg));
    }

    [Fact]
    public void Decode_still_accepts_a_well_formed_message_at_the_attribute_cap()
    {
        // Exactly the cap of zero-length attributes is well formed and must still decode — the rejection is
        // for exceeding the cap, not for reaching it.
        const int atCap = 64;
        var msg = new byte[20 + (atCap * 4)];
        WriteHeader(msg, atCap * 4);
        for (int i = 0; i < atCap; i++)
        {
            int at = 20 + (i * 4);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), 0x7F00);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), 0);
        }

        var decoded = Decode(msg);

        Assert.NotNull(decoded);
        Assert.Equal(atCap, decoded!.Attributes.Count);
    }

    // ── SIP-15 A2: encode length must not silently overflow the 16-bit field ───

    private static Core.Infrastructure.Stun.Messages.StunMessage BuildOversizedMessage()
    {
        // A single opaque attribute whose value overruns the 16-bit STUN message-length field
        // (RFC 5389 §6). value(65_532) + attr header(4) = 65_536 > 65_535 → the encoder must reject
        // rather than truncate-cast the body length into a corrupt ushort.
        var oversized = new UnknownRawAttribute(0x7F00) { Value = new byte[65_532] };
        return new Core.Infrastructure.Stun.Messages.StunMessage
        {
            MessageClass  = Core.Infrastructure.Stun.Messages.StunMessageClass.Request,
            MessageMethod = Core.Infrastructure.Stun.Messages.StunMessageMethod.Binding,
            TransactionId = new byte[12],
            Attributes    = [oversized],
        };
    }

    [Fact]
    public void Encode_message_body_over_16_bits_throws_instead_of_truncating()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new StunMessageCodec().Encode(BuildOversizedMessage()));
        Assert.Contains("65535", ex.Message);
    }

    [Fact]
    public void EncodeWithIntegrity_message_body_over_16_bits_throws_instead_of_truncating()
    {
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        Assert.Throws<InvalidOperationException>(
            () => new StunMessageCodec().EncodeWithIntegrity(BuildOversizedMessage(), key, addFingerprint: true));
    }

    [Fact]
    public void Encode_body_just_under_the_16_bit_limit_still_encodes()
    {
        // value(65_528, already 4-aligned) + attr header(4) = 65_532 body bytes — the largest body a
        // single padded attribute can produce — stays inside the 16-bit field and must encode.
        var nearLimit = new UnknownRawAttribute(0x7F00) { Value = new byte[65_528] };
        var message = new Core.Infrastructure.Stun.Messages.StunMessage
        {
            MessageClass  = Core.Infrastructure.Stun.Messages.StunMessageClass.Request,
            MessageMethod = Core.Infrastructure.Stun.Messages.StunMessageMethod.Binding,
            TransactionId = new byte[12],
            Attributes    = [nearLimit],
        };

        var encoded = new StunMessageCodec().Encode(message);
        Assert.Equal(65_532, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(2)));
    }
}
