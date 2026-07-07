using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="SipWireStreamFramer"/>. Stream framing over TCP/TLS is
/// the classic memory-exhaustion DoS surface: a peer can withhold the header terminator forever or
/// declare an oversized/overflowing Content-Length. The framer must bound buffered memory and abort
/// framing (throwing <see cref="InvalidOperationException"/>, which the transport turns into a
/// connection teardown) rather than buffer without bound.
/// </summary>
public sealed class SipWireStreamFramerFuzzTests
{
    private const int MaxMessageBytes = 262_144;

    private static byte[] ValidFrame(string body = "")
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        return Encoding.UTF8.GetBytes(
            "MESSAGE sip:bob@example.com SIP/2.0\r\n" +
            "Via: SIP/2.0/TCP host;branch=z9hG4bK1\r\n" +
            "Content-Length: " + bytes + "\r\n" +
            "\r\n" + body);
    }

    [Fact]
    public void ValidFrame_IsFramedExactly()
    {
        var framer = new SipWireStreamFramer();
        var frame = ValidFrame("hello");
        framer.Append(frame);

        Assert.True(framer.TryReadFrame(out var read));
        Assert.Equal(frame, read);
        Assert.False(framer.TryReadFrame(out _)); // buffer drained
    }

    [Fact]
    public void HeaderWithoutTerminator_IsBounded()
    {
        var framer = new SipWireStreamFramer();
        // A peer streams headers that never terminate (no CRLFCRLF).
        framer.Append(Encoding.ASCII.GetBytes(new string('A', MaxMessageBytes + 1)));

        ParserFuzz.WithinCallBudget(() =>
            Assert.Throws<InvalidOperationException>(() => framer.TryReadFrame(out _)));
    }

    [Theory]
    [InlineData("2000000000")]  // ~2 GB declared body
    [InlineData("2147483647")]  // int.MaxValue — would overflow headerLength + contentLength
    [InlineData("300000")]      // just over the cap
    public void OversizedContentLength_IsRejectedFast(string contentLength)
    {
        var framer = new SipWireStreamFramer();
        framer.Append(Encoding.UTF8.GetBytes(
            "MESSAGE sip:b@x SIP/2.0\r\n" +
            "Content-Length: " + contentLength + "\r\n" +
            "\r\n"));

        ParserFuzz.WithinCallBudget(() =>
            Assert.Throws<InvalidOperationException>(() => framer.TryReadFrame(out _)));
    }

    [Fact]
    public void NegativeContentLength_IsRejected()
    {
        var framer = new SipWireStreamFramer();
        framer.Append(Encoding.UTF8.GetBytes(
            "MESSAGE sip:b@x SIP/2.0\r\n" +
            "Content-Length: -1\r\n" +
            "\r\n"));

        Assert.Throws<InvalidOperationException>(() => framer.TryReadFrame(out _));
    }

    [Fact]
    public void RandomChunkedInput_TerminatesAndOnlyThrowsInvalidOperation()
    {
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var iteration = 0; iteration < 500; iteration++)
                {
                    var framer = new SipWireStreamFramer();
                    var totalChunks = rng.Next(1, 8);
                    for (var c = 0; c < totalChunks; c++)
                    {
                        var chunk = ParserFuzz.RandomBytes(rng, rng.Next(0, 512));
                        framer.Append(chunk);

                        // Drain whatever frames the buffer yields; only the documented protocol
                        // exception is permitted, and each call must terminate.
                        ParserFuzz.Guard(
                            () => { while (framer.TryReadFrame(out _)) { } },
                            typeof(InvalidOperationException));
                    }
                }
            }
        });
    }
}
