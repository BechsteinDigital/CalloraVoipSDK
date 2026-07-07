using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void ProtectAndUnprotect_RunConcurrentlyOnSharedInstance_StayCorrect()
    {
        const int count = 1000;
        using var context = new SrtpContext(CreateRfcMaterial());

        // Outbound direction (SSRC A): each seq maps deterministically to index == seq
        // while the sender index stays below 2^16, so a fixed reference is well defined.
        var outboundPlain = new byte[count][];
        var outboundExpected = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            outboundPlain[i] = CreateRtpPacket((ushort)i, ssrc: 0x11111111, payloadLength: 40);
            outboundExpected[i] = ProtectReference(outboundPlain[i], packetIndex: (ulong)i);
        }

        // Inbound direction (SSRC B): reference-protected packets to unprotect in order.
        var inboundPlain = new byte[count][];
        var inboundProtected = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            inboundPlain[i] = CreateRtpPacket((ushort)i, ssrc: 0x22222222, payloadLength: 40);
            inboundProtected[i] = ProtectReference(inboundPlain[i], packetIndex: (ulong)i);
        }

        var outboundActual = new byte[count][];
        var inboundActual = new byte[count][];

        // Send pump: many threads Protect in parallel. Receive pump: Unprotect in order.
        // Both run against the SAME instance at the same time.
        var sender = Task.Run(() =>
            Parallel.For(0, count, i => outboundActual[i] = context.Protect(outboundPlain[i])));

        var receiver = Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
                inboundActual[i] = context.Unprotect(inboundProtected[i]);
        });

        Task.WaitAll(sender, receiver);

        for (var i = 0; i < count; i++)
        {
            Assert.Equal(outboundExpected[i], outboundActual[i]);
            Assert.Equal(inboundPlain[i], inboundActual[i]);
        }
    }

    [Fact]
    public void Unprotect_ConcurrentDuplicatePackets_AcceptsExactlyOnce()
    {
        const int threads = 64;
        var packet = CreateRtpPacket(sequenceNumber: 5, ssrc: 0x0A0B0C0D, payloadLength: 16);
        var inbound = ProtectReference(packet, packetIndex: 5);
        using var context = new SrtpContext(CreateRfcMaterial());

        using var start = new ManualResetEventSlim(false);
        var accepted = 0;
        var replays = 0;
        var unexpectedFailures = 0;
        Exception? unexpected = null;
        byte[]? acceptedPlain = null;

        var workers = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    var plain = context.Unprotect(inbound);
                    Interlocked.Increment(ref accepted);
                    acceptedPlain = plain;
                }
                catch (SrtpReplayException)
                {
                    Interlocked.Increment(ref replays);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref unexpectedFailures);
                    Volatile.Write(ref unexpected, ex);
                }
            });
        }

        start.Set();
        Task.WaitAll(workers);

        Assert.True(unexpectedFailures == 0, $"Unexpected exception during concurrent Unprotect: {unexpected}");
        Assert.Equal(1, accepted);
        Assert.Equal(threads - 1, replays);
        Assert.Equal(packet, acceptedPlain);
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
