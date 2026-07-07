using System.Reflection;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for the private DNS response parser
/// <c>DnsSrvQuery.ParseSrvResponse</c>, which decodes untrusted DNS answers including compression
/// pointers. The parser is contractually non-throwing (returns an empty list on any failure) and
/// must always terminate — the classic risk being name-compression pointer loops. Reached via
/// reflection because the method is private.
/// </summary>
public sealed class DnsSrvResponseFuzzTests
{
    private const ushort TxId = 0x1234;

    private static readonly MethodInfo ParseSrvResponseMethod =
        typeof(DnsSrvQuery).GetMethod(
            "ParseSrvResponse",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ParseSrvResponse not found.");

    private static IReadOnlyList<DnsSrvRecord> ParseSrvResponse(byte[] data, ushort expectedTxId)
    {
        try
        {
            return (IReadOnlyList<DnsSrvRecord>)ParseSrvResponseMethod.Invoke(null, [data, expectedTxId])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real parser exception so the harness flags it as a robustness defect.
            throw ex.InnerException;
        }
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)labelBytes.Length);
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] BuildValidResponse()
    {
        var msg = new List<byte>
        {
            0x12, 0x34,             // txId
            0x81, 0x80,             // flags: QR=1, RD=1, RA=1, RCODE=0
            0x00, 0x01,             // qdCount = 1
            0x00, 0x01,             // anCount = 1
            0x00, 0x00,             // nsCount
            0x00, 0x00,             // arCount
        };

        // Question at offset 12.
        msg.AddRange(EncodeName("_sip._udp.example.com"));
        msg.AddRange([0x00, 0x21]); // QTYPE = SRV
        msg.AddRange([0x00, 0x01]); // QCLASS = IN

        // Answer: name is a compression pointer back to the question name at offset 12.
        msg.AddRange([0xC0, 0x0C]);
        msg.AddRange([0x00, 0x21]);             // TYPE = SRV
        msg.AddRange([0x00, 0x01]);             // CLASS = IN
        msg.AddRange([0x00, 0x00, 0x00, 0x3C]); // TTL

        var rdata = new List<byte>
        {
            0x00, 0x0A, // priority = 10
            0x00, 0x05, // weight = 5
            0x13, 0xC4, // port = 5060
        };
        rdata.AddRange(EncodeName("sipserver.example.com"));
        msg.AddRange([(byte)(rdata.Count >> 8), (byte)(rdata.Count & 0xFF)]);
        msg.AddRange(rdata);

        return [.. msg];
    }

    [Fact]
    public void ValidResponse_ParsesSrvRecord()
    {
        var records = ParseSrvResponse(BuildValidResponse(), TxId);
        Assert.Single(records);
        Assert.Equal(5060, records[0].Port);
        Assert.Equal(10, records[0].Priority);
        Assert.Equal("sipserver.example.com", records[0].Target);
    }

    [Fact]
    public void WrongTransactionId_ReturnsEmpty()
    {
        var records = ParseSrvResponse(BuildValidResponse(), 0x9999);
        Assert.Empty(records);
    }

    [Fact]
    public void Truncation_NeverThrows_AndTerminates()
    {
        var valid = BuildValidResponse();
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= valid.Length; len++)
            {
                var prefix = valid[..len];
                ParserFuzz.Guard(() => ParseSrvResponse(prefix, TxId));
            }
        });
    }

    [Fact]
    public void RandomBytes_NeverThrow_AndTerminate()
    {
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 3_000; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, rng.Next(0, 600));
                    // Match txId half the time so the answer section is actually parsed.
                    if ((i & 1) == 0 && data.Length >= 2)
                    {
                        data[0] = 0x12;
                        data[1] = 0x34;
                    }

                    ParserFuzz.Guard(() => ParseSrvResponse(data, TxId));
                }
            }
        });
    }

    [Fact]
    public void SelfReferentialCompressionPointer_Terminates()
    {
        // Answer target is a compression pointer that points at itself: 0xC0 0x?? where the pointer
        // target is the pointer byte. The single-follow limit must break the loop.
        var msg = new List<byte>
        {
            0x12, 0x34, 0x81, 0x80,
            0x00, 0x00,             // qdCount = 0 (skip question)
            0x00, 0x01,             // anCount = 1
            0x00, 0x00, 0x00, 0x00,
        };

        // Answer name = root label (offset 12), then SRV RR header.
        msg.Add(0x00);                          // name root at offset 12
        msg.AddRange([0x00, 0x21]);             // TYPE = SRV
        msg.AddRange([0x00, 0x01]);             // CLASS = IN
        msg.AddRange([0x00, 0x00, 0x00, 0x3C]); // TTL

        // RDATA offset begins after the 2-byte RDLEN we are about to add.
        var rdataOffset = msg.Count + 2;
        var pointerSelfOffset = rdataOffset + 6; // where the target pointer's first byte lands
        var rdata = new List<byte>
        {
            0x00, 0x0A, 0x00, 0x05, 0x13, 0xC4, // priority, weight, port
            0xC0, (byte)pointerSelfOffset,       // target = pointer to itself
        };
        msg.AddRange([(byte)(rdata.Count >> 8), (byte)(rdata.Count & 0xFF)]);
        msg.AddRange(rdata);

        ParserFuzz.WithinCallBudget(() => ParseSrvResponse([.. msg], TxId));
    }

    [Fact]
    public void HugeAnswerCount_WithTinyData_Terminates_AndNoOversizedAllocation()
    {
        // anCount claims 65535 answers but the datagram carries none. The pre-allocation must be
        // capped to the datagram's real capacity and the loop must exit on the bounds guard.
        byte[] data =
        [
            0x12, 0x34, 0x81, 0x80,
            0x00, 0x00,             // qdCount = 0
            0xFF, 0xFF,             // anCount = 65535
            0x00, 0x00, 0x00, 0x00,
        ];
        ParserFuzz.WithinCallBudget(() => Assert.Empty(ParseSrvResponse(data, TxId)));
    }

    [Fact]
    public void SrvRecordWithShortRdLength_DoesNotOverRead()
    {
        // Regression: an SRV RR with RDLEN < 6 positioned at the end of the buffer previously drove
        // the priority/weight/port reads past the span. It must now be skipped without throwing.
        byte[] data =
        [
            0xAB, 0xCD, 0x81, 0x80,
            0x00, 0x00,             // qdCount = 0
            0x00, 0x01,             // anCount = 1
            0x00, 0x00, 0x00, 0x00,
            0x00,                   // answer name = root (offset 12)
            0x00, 0x21,             // TYPE = SRV
            0x00, 0x01,             // CLASS = IN
            0x00, 0x00, 0x00, 0x3C, // TTL
            0x00, 0x04,             // RDLEN = 4 (< 6)
            0x00, 0x0A, 0x00, 0x05, // 4 RDATA bytes, no room for port
        ];
        ParserFuzz.WithinCallBudget(() => Assert.Empty(ParseSrvResponse(data, 0xABCD)));
    }
}
