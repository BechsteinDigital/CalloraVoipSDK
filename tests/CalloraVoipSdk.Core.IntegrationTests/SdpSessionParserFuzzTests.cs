using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Robustness (fuzz) tests for <see cref="SdpSessionParser"/>. SDP arrives inside untrusted SIP
/// bodies. <c>Parse</c> may reject malformed input with <see cref="FormatException"/>, but must
/// never throw any other exception type and must always terminate.
/// </summary>
public sealed class SdpSessionParserFuzzTests
{
    private const string ValidSdp =
        "v=0\r\n" +
        "o=- 20518 0 IN IP4 203.0.113.1\r\n" +
        "s=-\r\n" +
        "c=IN IP4 203.0.113.1\r\n" +
        "t=0 0\r\n" +
        "m=audio 49170 RTP/AVP 0 8 101\r\n" +
        "a=rtpmap:0 PCMU/8000\r\n" +
        "a=rtpmap:8 PCMA/8000\r\n" +
        "a=rtpmap:101 telephone-event/8000\r\n" +
        "a=sendrecv\r\n";

    [Fact]
    public void ValidSdp_Parses()
    {
        var parser = new SdpSessionParser();
        var parsed = parser.Parse(ValidSdp);
        Assert.Single(parsed.Media);
        Assert.Equal(49170, parsed.Media[0].Port);
    }

    [Fact]
    public void DuplicatePayloadTypes_DoNotCrash()
    {
        // Regression: an m-line repeating a payload type previously threw ArgumentException from
        // ToDictionary (duplicate key). It must now parse gracefully.
        var parser = new SdpSessionParser();
        const string sdp =
            "v=0\r\n" +
            "o=- 1 0 IN IP4 203.0.113.1\r\n" +
            "s=-\r\n" +
            "c=IN IP4 203.0.113.1\r\n" +
            "t=0 0\r\n" +
            "m=audio 49170 RTP/AVP 0 0 8 8 0\r\n";

        SdpSessionDescription? result = null;
        ParserFuzz.WithinCallBudget(() => result = parser.Parse(sdp));
        Assert.NotNull(result);
        Assert.Single(result!.Media);
    }

    [Fact]
    public void Truncation_OnlyThrowsFormatException()
    {
        var parser = new SdpSessionParser();
        ParserFuzz.CompletesWithin(20_000, () =>
        {
            for (var len = 0; len <= ValidSdp.Length; len++)
            {
                var prefix = ValidSdp[..len];
                ParserFuzz.Guard(() => parser.Parse(prefix), typeof(FormatException), typeof(ArgumentException));
            }
        });
    }

    [Fact]
    public void RandomInput_OnlyThrowsFormatException_AndTerminates()
    {
        var parser = new SdpSessionParser();
        ParserFuzz.CompletesWithin(30_000, () =>
        {
            foreach (var seed in ParserFuzz.Seeds)
            {
                var rng = new Random(seed);
                for (var i = 0; i < 2_000; i++)
                {
                    var bytes = ParserFuzz.RandomBytes(rng, rng.Next(0, 2_048));
                    var text = Encoding.Latin1.GetString(bytes);
                    ParserFuzz.Guard(
                        () => parser.Parse(text),
                        typeof(FormatException),
                        typeof(ArgumentException));
                }
            }
        });
    }

    [Theory]
    [InlineData("m=audio 99999999999999999999 RTP/AVP 0\r\n")] // port overflows int
    [InlineData("m=audio 49170 RTP/AVP\r\n")]                   // no payload types
    [InlineData("m=\r\n")]                                       // empty media line
    [InlineData("a=rtpmap:\r\nm=audio 1 RTP/AVP 0\r\n")]
    [InlineData("b=AS:99999999999999999\r\n")]
    public void HostileFields_DoNotThrowUnexpectedly(string fragment)
    {
        var parser = new SdpSessionParser();
        var sdp = "v=0\r\no=- 1 0 IN IP4 203.0.113.1\r\ns=-\r\nt=0 0\r\n" + fragment;
        ParserFuzz.Guard(() => parser.Parse(sdp), typeof(FormatException), typeof(ArgumentException));
    }
}
