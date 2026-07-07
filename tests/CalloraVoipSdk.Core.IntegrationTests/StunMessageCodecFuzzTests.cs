using System.Linq;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="StunMessageCodec"/>. STUN packets are untrusted input on
/// the ICE/TURN receive path; <c>Decode</c> is contractually non-throwing (returns <c>null</c> on
/// malformed input) because its callers do not wrap it in a catch. Fields under attack: the
/// declared length, per-attribute lengths, and the address-family byte of MAPPED-ADDRESS /
/// XOR-MAPPED-ADDRESS / ALTERNATE-SERVER attributes.
/// </summary>
public sealed class StunMessageCodecFuzzTests
{
    private static byte[] ValidBindingRequestHeader()
    {
        var msg = new List<byte>
        {
            0x00, 0x01,             // type = Binding request
            0x00, 0x00,             // length = 0
            0x21, 0x12, 0xA4, 0x42, // magic cookie
        };
        msg.AddRange(Enumerable.Repeat((byte)0xAB, 12)); // transaction id
        return [.. msg];
    }

    private static byte[] WithSingleAttribute(byte[] attribute)
    {
        var msg = ValidBindingRequestHeader().ToList();
        msg.AddRange(attribute);
        var declaredLen = attribute.Length;
        msg[2] = (byte)(declaredLen >> 8);
        msg[3] = (byte)(declaredLen & 0xFF);
        return [.. msg];
    }

    [Fact]
    public void ValidHeader_Decodes()
    {
        var codec = new StunMessageCodec();
        var message = codec.Decode(ValidBindingRequestHeader());
        Assert.NotNull(message);
    }

    [Fact]
    public void XorMappedAddress_Ipv4Family_ButTooShort_DoesNotThrow()
    {
        // Regression: family byte says IPv4 but only 4 value bytes are present (no address).
        // DecodeMappedAddress previously sliced value[4..8] and threw ArgumentOutOfRangeException.
        var codec = new StunMessageCodec();
        byte[] attr = [0x00, 0x20, 0x00, 0x04, 0x00, 0x01, 0x1F, 0x40];
        var message = WithSingleAttribute(attr);

        StunMessage? decoded = null;
        ParserFuzz.WithinCallBudget(() => decoded = codec.Decode(message));
        Assert.NotNull(decoded);
    }

    [Fact]
    public void MappedAddress_Ipv6Family_ButTooShort_DoesNotThrow()
    {
        // Regression: family byte says IPv6 but only 8 value bytes are present.
        var codec = new StunMessageCodec();
        byte[] attr = [0x00, 0x01, 0x00, 0x08, 0x00, 0x02, 0x1F, 0x40, 0x0A, 0x0B, 0x0C, 0x0D];
        var message = WithSingleAttribute(attr);

        StunMessage? decoded = null;
        ParserFuzz.WithinCallBudget(() => decoded = codec.Decode(message));
        Assert.NotNull(decoded);
    }

    [Fact]
    public void AttributeLengthBeyondPacket_StopsGracefully()
    {
        var codec = new StunMessageCodec();
        // Attribute header claims 0xFFFF value bytes with none present.
        byte[] attr = [0x00, 0x20, 0xFF, 0xFF];
        var message = WithSingleAttribute(attr);
        Assert.NotNull(codec.Decode(message));
    }

    [Fact]
    public void Truncation_NeverThrows()
    {
        var codec = new StunMessageCodec();
        byte[] attr = [0x00, 0x20, 0x00, 0x08, 0x00, 0x01, 0x1F, 0x40, 0x0A, 0x00, 0x00, 0x01];
        var full = WithSingleAttribute(attr);
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= full.Length; len++)
            {
                var prefix = full[..len];
                ParserFuzz.Guard(() => codec.Decode(prefix));
            }
        });
    }

    [Fact]
    public void RandomBytes_NeverThrow_AndTerminate()
    {
        var codec = new StunMessageCodec();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 3_000; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, rng.Next(0, 1_500));
                    // Decode must never throw; the verify helpers are also on the untrusted path.
                    ParserFuzz.Guard(() => codec.Decode(data));
                    ParserFuzz.Guard(() => codec.VerifyFingerprint(data));
                    ParserFuzz.Guard(() => codec.VerifyIntegrity(data, data));
                }
            }
        });
    }

    [Fact]
    public void RandomBytesWithValidMagicCookie_NeverThrow()
    {
        // Force the magic-cookie gate open so the attribute parser is reached with hostile lengths.
        var codec = new StunMessageCodec();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 3_000; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, rng.Next(20, 600));
                    data[4] = 0x21;
                    data[5] = 0x12;
                    data[6] = 0xA4;
                    data[7] = 0x42;
                    // Keep declared length within the buffer so DecodeAttributes runs.
                    var maxAttr = data.Length - 20;
                    data[2] = (byte)(maxAttr >> 8);
                    data[3] = (byte)(maxAttr & 0xFF);
                    ParserFuzz.Guard(() => codec.Decode(data));
                }
            }
        });
    }
}
