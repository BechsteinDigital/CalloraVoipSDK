using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The SDP trust-boundary helpers used to swallow every exception from unparseable remote SDP with a
/// bare <c>catch { }</c> (HARD-G3). They must stay robust — never throw on malformed remote input —
/// but no longer be silent: a discarded parse is now logged via the optional logger, while the
/// happy path logs nothing.
/// </summary>
public sealed class SdpParseFailureLoggingTests
{
    // m-line with fewer than four tokens makes the parser throw (FormatException) mid-parse.
    private const string MalformedSdp = "v=0\nm=audio 9";

    private const string ValidSdp =
        "v=0\n" +
        "o=- 0 0 IN IP4 127.0.0.1\n" +
        "s=-\n" +
        "c=IN IP4 127.0.0.1\n" +
        "t=0 0\n" +
        "m=audio 40000 RTP/AVP 0\n" +
        "a=rtpmap:0 PCMU/8000\n";

    private static IPEndPoint Local => new(IPAddress.Loopback, 40001);

    [Fact]
    public void TryParseMediaParameters_on_malformed_sdp_returns_null_and_logs()
    {
        var logger = new CapturingLogger();

        var result = SdpUtilities.TryParseMediaParameters(MalformedSdp, Local, logger: logger);

        Assert.Null(result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void TryParseMediaParameters_on_valid_sdp_succeeds_without_logging()
    {
        var logger = new CapturingLogger();

        var result = SdpUtilities.TryParseMediaParameters(ValidSdp, Local, logger: logger);

        Assert.NotNull(result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TryBuildNegotiatedAnswer_on_malformed_sdp_returns_null_and_logs()
    {
        var logger = new CapturingLogger();

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(MalformedSdp, Local, hold: false, logger: logger);

        Assert.Null(answer);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void TryInspectAudioSecurity_on_malformed_sdp_returns_false_and_logs()
    {
        var logger = new CapturingLogger();

        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            MalformedSdp, out var isSrtpSignaled, out _, logger);

        Assert.False(inspected);
        Assert.False(isSrtpSignaled);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void IsRemoteHoldSdp_on_malformed_sdp_falls_back_to_substring_and_logs()
    {
        var logger = new CapturingLogger();

        // Parser throws on the m-line before reaching a=sendonly; the fallback substring probe
        // must still detect hold intent, and the fallback must be logged.
        var hold = SdpUtilities.IsRemoteHoldSdp("v=0\nm=audio 9\na=sendonly", logger);

        Assert.True(hold);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }
}
