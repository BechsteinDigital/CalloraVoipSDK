using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.IntegrationTests;

public sealed class SrtpContextTests
{
    private const int AuthTagLength = 10;

    private static readonly byte[] RfcMasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] RfcMasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    private static readonly byte[] RfcCipherKey = Convert.FromHexString("C61E7A93744F39EE10734AFE3FF7A087");
    private static readonly byte[] RfcCipherSalt = Convert.FromHexString("30CBBC08863D8C85D49DB34A9AE1");
    private static readonly byte[] RfcAuthKey20 = Convert.FromHexString("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4");

    [Fact]
    public void Derive_UsesRfc3711InitialKeyDerivationVector()
    {
        var keys = SrtpKeyDerivation.Derive(CreateRfcMaterial());

        Assert.Equal(RfcCipherKey, keys.CipherKey);
        Assert.Equal(RfcCipherSalt, keys.Salt);
        Assert.Equal(RfcAuthKey20, keys.AuthKey);
    }

    [Fact]
    public void Protect_MatchesReferenceForPayloadAcrossMultipleAesBlocks()
    {
        var packet = CreateRtpPacket(sequenceNumber: 0, ssrc: 0, payloadLength: 48);
        var context = new SrtpContext(CreateRfcMaterial());

        var protectedPacket = context.Protect(packet);
        var expected = ProtectReference(packet, packetIndex: 0);

        Assert.Equal(expected, protectedPacket);
    }

    [Fact]
    public void Protect_AuthenticatesRocForSameSequenceNumberAfterWrap()
    {
        var seq0Packet = CreateRtpPacket(sequenceNumber: 0, ssrc: 0x01020304, payloadLength: 0);
        var seq65535Packet = CreateRtpPacket(sequenceNumber: ushort.MaxValue, ssrc: 0x01020304, payloadLength: 0);
        var initialContext = new SrtpContext(CreateRfcMaterial());
        var wrappedContext = new SrtpContext(CreateRfcMaterial());

        var initialProtected = initialContext.Protect(seq0Packet);
        wrappedContext.Protect(seq65535Packet);
        var wrappedProtected = wrappedContext.Protect(seq0Packet);

        Assert.NotEqual(GetAuthTag(initialProtected), GetAuthTag(wrappedProtected));
        Assert.Equal(ProtectReference(seq0Packet, packetIndex: 65_536), wrappedProtected);
    }

    [Fact]
    public void Unprotect_VerifiesReferencePacketAfterSequenceWrap()
    {
        var seq65535Packet = CreateRtpPacket(sequenceNumber: ushort.MaxValue, ssrc: 0x01020304, payloadLength: 16);
        var seq0Packet = CreateRtpPacket(sequenceNumber: 0, ssrc: 0x01020304, payloadLength: 16);
        var protectedSeq65535 = ProtectReference(seq65535Packet, packetIndex: 65_535);
        var protectedSeq0AfterWrap = ProtectReference(seq0Packet, packetIndex: 65_536);
        var context = new SrtpContext(CreateRfcMaterial());

        Assert.Equal(seq65535Packet, context.Unprotect(protectedSeq65535));
        Assert.Equal(seq0Packet, context.Unprotect(protectedSeq0AfterWrap));
    }

    private static SrtpKeyMaterial CreateRfcMaterial() =>
        new()
        {
            MasterKey = RfcMasterKey,
            MasterSalt = RfcMasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80
        };

    private static byte[] CreateRtpPacket(ushort sequenceNumber, uint ssrc, int payloadLength)
    {
        var packet = new byte[12 + payloadLength];
        packet[0] = 0x80;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);

        for (var i = 0; i < payloadLength; i++)
            packet[12 + i] = (byte)i;

        return packet;
    }

    private static byte[] ProtectReference(byte[] rtpPacket, ulong packetIndex)
    {
        var result = new byte[rtpPacket.Length + AuthTagLength];
        rtpPacket.CopyTo(result.AsSpan());

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.AsSpan(8));
        var iv = BuildIvReference(ssrc, packetIndex);
        AesCmXorReference(result.AsSpan(12, rtpPacket.Length - 12), iv);

        var tag = ComputeAuthTagReference(result.AsSpan(0, rtpPacket.Length), (uint)(packetIndex >> 16));
        tag.AsSpan(0, AuthTagLength).CopyTo(result.AsSpan(rtpPacket.Length));
        return result;
    }

    private static byte[] BuildIvReference(uint ssrc, ulong packetIndex)
    {
        var iv = new byte[16];
        RfcCipherSalt.CopyTo(iv.AsSpan());

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >> 8);
        iv[7] ^= (byte)ssrc;

        iv[8] ^= (byte)(packetIndex >> 40);
        iv[9] ^= (byte)(packetIndex >> 32);
        iv[10] ^= (byte)(packetIndex >> 24);
        iv[11] ^= (byte)(packetIndex >> 16);
        iv[12] ^= (byte)(packetIndex >> 8);
        iv[13] ^= (byte)packetIndex;
        return iv;
    }

    private static void AesCmXorReference(Span<byte> data, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = RfcCipherKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var counterBlock = (byte[])iv.Clone();
        var keyStreamBlock = new byte[16];
        var offset = 0;
        var counter = 0;
        while (offset < data.Length)
        {
            counterBlock[14] = (byte)(counter >> 8);
            counterBlock[15] = (byte)counter;
            encryptor.TransformBlock(counterBlock, 0, counterBlock.Length, keyStreamBlock, 0);

            var chunk = Math.Min(keyStreamBlock.Length, data.Length - offset);
            for (var i = 0; i < chunk; i++)
                data[offset + i] ^= keyStreamBlock[i];

            offset += chunk;
            counter++;
        }
    }

    private static byte[] ComputeAuthTagReference(ReadOnlySpan<byte> authenticatedPortion, uint roc)
    {
        var authInput = new byte[authenticatedPortion.Length + 4];
        authenticatedPortion.CopyTo(authInput);
        BinaryPrimitives.WriteUInt32BigEndian(authInput.AsSpan(authenticatedPortion.Length), roc);
        return HMACSHA1.HashData(RfcAuthKey20, authInput);
    }

    private static byte[] GetAuthTag(byte[] protectedPacket) => protectedPacket[^AuthTagLength..];
}
