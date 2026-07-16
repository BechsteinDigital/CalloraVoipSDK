using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Wire-bounds gate for <see cref="StunMessageCodec"/> against malformed attacker input (HARD-A3/A4).
/// A truncated XOR-MAPPED-ADDRESS must not throw out of the decoder, and a MESSAGE-INTEGRITY whose
/// adjusted length would overflow the 16-bit STUN length field must be rejected rather than verified
/// against a silently wrapped length.
/// </summary>
public sealed class StunMessageCodecWireBoundsTests
{
    private const uint MagicCookie = 0x2112A442;

    private static byte[] BuildMessage(ushort attrType, byte[] attrValue)
    {
        var aligned = (attrValue.Length + 3) & ~3;
        var msg = new byte[20 + 4 + aligned];
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0), 0x0101);              // Binding success response
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(4 + aligned)); // message length
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
        for (byte i = 0; i < 12; i++) msg[8 + i] = (byte)(i + 1);                  // transaction id
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), (ushort)attrValue.Length);
        attrValue.CopyTo(msg.AsSpan(24));
        return msg;
    }

    [Fact]
    public void Decode_truncated_ipv6_xor_mapped_address_does_not_throw()
    {
        // family=0x02 (IPv6) but only 8 value bytes present (needs 20): the decoder must fall back
        // to an UnknownRawAttribute instead of slicing value[4..20] out of bounds.
        var value = new byte[] { 0x00, 0x02, 0x12, 0x34, 0xAA, 0xBB, 0xCC, 0xDD };
        var message = BuildMessage((ushort)StunAttributeType.XorMappedAddress, value);

        var decoded = StunCodecDecode(message);

        var attr = Assert.Single(decoded!.Attributes);
        var unknown = Assert.IsType<UnknownRawAttribute>(attr);
        Assert.Equal((ushort)StunAttributeType.XorMappedAddress, unknown.RawAttributeType);
    }

    [Fact]
    public void Decode_truncated_ipv4_xor_mapped_address_does_not_throw()
    {
        // family=0x01 (IPv4) but only 4 value bytes present (needs 8).
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34 };
        var message = BuildMessage((ushort)StunAttributeType.XorMappedAddress, value);

        var decoded = StunCodecDecode(message);

        Assert.IsType<UnknownRawAttribute>(Assert.Single(decoded!.Attributes));
    }

    [Fact]
    public void Decode_valid_ipv4_xor_mapped_address_still_decodes()
    {
        // Happy path must survive the added length guards.
        var value = new byte[] { 0x00, 0x01, 0x12, 0x34, 0xDE, 0xAD, 0xBE, 0xEF };
        var message = BuildMessage((ushort)StunAttributeType.XorMappedAddress, value);

        var decoded = StunCodecDecode(message);

        Assert.IsType<XorMappedAddressAttribute>(Assert.Single(decoded!.Attributes));
    }

    [Fact]
    public void VerifyIntegrity_rejects_message_integrity_beyond_16bit_length()
    {
        // Place MESSAGE-INTEGRITY at an offset where the RFC 5389 §15.4 adjusted length would exceed
        // the 16-bit STUN length field (65536 > 65535). The stored HMAC is exactly what a codec that
        // silently wrapped the ushort cast (adjusted length → 0) would compute, so an unguarded codec
        // would ACCEPT this forgery. The overflow guard must REJECT it.
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        const int paddingAttrLen = 65508;             // pushes the MI attribute to offset 65532
        const int miOffset = 20 + 4 + paddingAttrLen; // 65532
        var message = new byte[miOffset + 4 + 20];    // + MI header + 20-byte HMAC
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), 0x0101);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        for (byte i = 0; i < 12; i++) message[8 + i] = (byte)(i + 1);
        // Benign padding attribute (Software) that carries the offset past the 16-bit boundary.
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20), (ushort)StunAttributeType.Software);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22), paddingAttrLen);
        // MESSAGE-INTEGRITY header at miOffset.
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(miOffset), (ushort)StunAttributeType.MessageIntegrity);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(miOffset + 2), 20);

        // Forge the HMAC the buggy (wrapped length = 65536 & 0xFFFF = 0) path would have produced.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key);
        hmac.AppendData(message.AsSpan(0, 2));
        hmac.AppendData(new byte[] { 0x00, 0x00 }); // wrapped adjusted length
        hmac.AppendData(message.AsSpan(4, miOffset - 4));
        var forged = hmac.GetHashAndReset();
        forged.CopyTo(message.AsSpan(miOffset + 4));

        var codec = new StunMessageCodec();

        Assert.False(codec.VerifyIntegrity(message, key));
    }

    private static Core.Infrastructure.Stun.Messages.StunMessage? StunCodecDecode(byte[] message)
        => new StunMessageCodec().Decode(message);
}
