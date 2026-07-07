using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="SipWireProtocol"/>. <c>TryParseRequest</c>/
/// <c>TryParseResponse</c> consume untrusted datagram bytes and are contractually
/// non-throwing: they must return <c>true</c>/<c>false</c> for any input, never throw,
/// and always terminate.
/// </summary>
public sealed class SipWireProtocolFuzzTests
{
    private static readonly byte[] ValidRequest = Encoding.UTF8.GetBytes(
        "INVITE sip:bob@example.com SIP/2.0\r\n" +
        "Via: SIP/2.0/UDP host.example.com;branch=z9hG4bK1\r\n" +
        "Max-Forwards: 70\r\n" +
        "From: Alice <sip:alice@example.com>;tag=1928\r\n" +
        "To: Bob <sip:bob@example.com>\r\n" +
        "Call-ID: a84b4c76e66710@host\r\n" +
        "CSeq: 314159 INVITE\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n");

    [Fact]
    public void ValidRequest_ParsesSuccessfully()
    {
        var codec = new SipWireProtocol();
        Assert.True(codec.TryParseRequest(ValidRequest, out var request));
        Assert.NotNull(request);
        Assert.Equal("INVITE", request!.Method);
    }

    [Fact]
    public void Truncation_NeverThrows()
    {
        var codec = new SipWireProtocol();
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= ValidRequest.Length; len++)
            {
                var prefix = ValidRequest[..len];
                ParserFuzz.Guard(() => codec.TryParseRequest(prefix, out _));
                ParserFuzz.Guard(() => codec.TryParseResponse(prefix, out _));
            }
        });
    }

    [Fact]
    public void RandomBytes_NeverThrow_AndTerminate()
    {
        var codec = new SipWireProtocol();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 2_000; i++)
                {
                    var length = rng.Next(0, 4_096);
                    var data = ParserFuzz.RandomBytes(rng, length);
                    ParserFuzz.Guard(() => codec.TryParseRequest(data, out _));
                    ParserFuzz.Guard(() => codec.TryParseResponse(data, out _));
                }

                // A handful of very large inputs (beyond MaxMessageBytes) must still be rejected fast.
                for (var i = 0; i < 4; i++)
                {
                    var data = ParserFuzz.RandomBytes(rng, 200_000);
                    ParserFuzz.Guard(() => codec.TryParseRequest(data, out _));
                }
            }
        });
    }

    [Theory]
    [InlineData("Content-Length: 999999999999999999999999")] // overflows int → must be rejected, not throw
    [InlineData("Content-Length: -5")]                        // negative
    [InlineData("Content-Length: 1000000")]                   // far exceeds actual body
    [InlineData("Content-Length: not-a-number")]
    public void HostileContentLength_ReturnsFalse_WithoutThrowing(string contentLengthHeader)
    {
        var codec = new SipWireProtocol();
        var message = Encoding.UTF8.GetBytes(
            "INVITE sip:bob@example.com SIP/2.0\r\n" +
            "Via: SIP/2.0/UDP host;branch=z9hG4bK1\r\n" +
            "From: <sip:a@x>;tag=1\r\n" +
            "To: <sip:b@x>\r\n" +
            "Call-ID: abc\r\n" +
            "CSeq: 1 INVITE\r\n" +
            contentLengthHeader + "\r\n" +
            "\r\nBODY");

        var parsed = false;
        ParserFuzz.WithinCallBudget(() => parsed = codec.TryParseRequest(message, out _));
        Assert.False(parsed);
    }
}
