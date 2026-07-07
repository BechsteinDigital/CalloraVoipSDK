using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="RtcpPacketCodec"/>. RTCP compound packets are untrusted
/// input decoded by the quality monitor, whose expected failures are
/// <see cref="ArgumentException"/> (malformed) and <see cref="NotSupportedException"/> (unknown
/// packet type). Fields under attack: the length word, the RC/SC report/source count, SDES item
/// lengths, and padding count. No input may hang or throw any other exception.
/// </summary>
public sealed class RtcpPacketCodecFuzzTests
{
    private static byte[] ValidReceiverReport()
    {
        // V=2, P=0, RC=0 | PT=201 (RR) | length=1 => (1+1)*4 = 8 bytes total | SSRC
        return
        [
            0x80, 0xC9, 0x00, 0x01,
            0xDE, 0xAD, 0xBE, 0xEF,
        ];
    }

    [Fact]
    public void ValidReceiverReport_Decodes()
    {
        var codec = new RtcpPacketCodec();
        var packets = codec.Decode(ValidReceiverReport());
        Assert.Single(packets);
    }

    [Fact]
    public void Truncation_OnlyThrowsExpected()
    {
        var codec = new RtcpPacketCodec();
        var valid = ValidReceiverReport();
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= valid.Length; len++)
            {
                var prefix = valid[..len];
                ParserFuzz.Guard(
                    () => codec.Decode(prefix),
                    typeof(ArgumentException),
                    typeof(NotSupportedException));
            }
        });
    }

    [Fact]
    public void RandomBytes_OnlyThrowExpected_AndTerminate()
    {
        var codec = new RtcpPacketCodec();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 3_000; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, rng.Next(0, 1_500));
                    ParserFuzz.Guard(
                        () => codec.Decode(data),
                        typeof(ArgumentException),
                        typeof(NotSupportedException));
                }
            }
        });
    }

    [Fact]
    public void InconsistentLengthField_IsRejected()
    {
        var codec = new RtcpPacketCodec();
        // length=0xFFFF claims 262144 bytes, only 8 present.
        byte[] data =
        [
            0x80, 0xC9, 0xFF, 0xFF,
            0xDE, 0xAD, 0xBE, 0xEF,
        ];
        ParserFuzz.WithinCallBudget(() =>
            Assert.Throws<ArgumentException>(() => codec.Decode(data)));
    }

    [Fact]
    public void MaxReportCountWithShortBody_IsRejected()
    {
        var codec = new RtcpPacketCodec();
        // RC=31 declared but the packet only carries an SSRC and no report blocks.
        byte[] data =
        [
            0x9F, 0xC9, 0x00, 0x01,
            0xDE, 0xAD, 0xBE, 0xEF,
        ];
        Assert.Throws<ArgumentException>(() => codec.Decode(data));
    }

    [Fact]
    public void UnknownPacketType_ThrowsNotSupported()
    {
        var codec = new RtcpPacketCodec();
        // PT=205 (RTPFB) is a valid but unsupported RTCP type.
        byte[] data =
        [
            0x80, 0xCD, 0x00, 0x01,
            0xDE, 0xAD, 0xBE, 0xEF,
        ];
        Assert.Throws<NotSupportedException>(() => codec.Decode(data));
    }
}
