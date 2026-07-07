using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="RtpPacketCodec"/>. RTP datagrams are untrusted UDP input
/// decoded inside the media receive loop, whose only expected failure is
/// <see cref="FormatException"/>. Any other exception would escape the loop; a hang would stall
/// media. Fields under attack: CSRC count, header-extension length, and padding count.
/// </summary>
public sealed class RtpPacketCodecFuzzTests
{
    private static byte[] ValidPacket()
    {
        // V=2, P=0, X=0, CC=0 | M=0, PT=0(PCMU) | seq | ts | ssrc | 4-byte payload
        return
        [
            0x80, 0x00, 0x12, 0x34,
            0x00, 0x00, 0x00, 0x64,
            0xDE, 0xAD, 0xBE, 0xEF,
            0x01, 0x02, 0x03, 0x04,
        ];
    }

    [Fact]
    public void ValidPacket_Decodes()
    {
        var codec = new RtpPacketCodec();
        var packet = codec.Decode(ValidPacket());
        Assert.Equal(2, packet.Version);
        Assert.Equal(4, packet.Payload.Length);
    }

    [Fact]
    public void Truncation_OnlyThrowsFormatException()
    {
        var codec = new RtpPacketCodec();
        var valid = ValidPacket();
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= valid.Length; len++)
            {
                var prefix = valid[..len];
                ParserFuzz.Guard(() => codec.Decode(prefix), typeof(FormatException));
            }
        });
    }

    [Fact]
    public void RandomBytes_OnlyThrowFormatException_AndTerminate()
    {
        var codec = new RtpPacketCodec();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 3_000; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, rng.Next(0, 1_500));
                    ParserFuzz.Guard(() => codec.Decode(data), typeof(FormatException));
                }
            }
        });
    }

    [Fact]
    public void HugeHeaderExtensionLength_IsRejected_NotOverRead()
    {
        var codec = new RtpPacketCodec();
        // X=1 set, extension length word = 0xFFFF (claims 262140 extension bytes) but datagram tiny.
        byte[] data =
        [
            0x90, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xBE, 0xDE, 0xFF, 0xFF, // ext profile 0xBEDE, ext length 0xFFFF words
        ];
        ParserFuzz.WithinCallBudget(() =>
            Assert.Throws<FormatException>(() => codec.Decode(data)));
    }

    [Fact]
    public void MaxCsrcCountWithoutData_IsRejected()
    {
        var codec = new RtpPacketCodec();
        // CC=15 declared, but no CSRC bytes present beyond the 12-byte header.
        byte[] data =
        [
            0x8F, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Throws<FormatException>(() => codec.Decode(data));
    }

    [Fact]
    public void InvalidPaddingCount_IsRejected()
    {
        var codec = new RtpPacketCodec();
        // P=1 set; final byte claims 200 padding bytes on a 4-byte payload.
        byte[] data =
        [
            0xA0, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x01, 0x02, 0x03, 0xC8,
        ];
        Assert.Throws<FormatException>(() => codec.Decode(data));
    }
}
