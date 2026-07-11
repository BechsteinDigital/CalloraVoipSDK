using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins CORE-019 (outbound part): consumer-supplied custom headers reach the outbound INVITE, while
/// protected dialog/transport headers cannot be overridden and header-injection attempts are dropped.
/// </summary>
public sealed class SipCustomHeaderTests
{
    private static Dictionary<string, string> BuildInviteHeaders(IReadOnlyDictionary<string, string> custom)
    {
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            CustomHeaders = custom
        };
        var headerService = new SipCallSessionHeaderService(context);

        return headerService.CreateDialogRequestHeaders(
            method: "INVITE",
            cseq: 1,
            branch: "z9hG4bK-test",
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: true);
    }

    [Fact]
    public void Custom_header_is_added_to_the_invite()
    {
        var headers = BuildInviteHeaders(new Dictionary<string, string>
        {
            ["X-Trunk-Id"] = "acme-42",
            ["X-Session-Tag"] = "campaign-7"
        });

        Assert.Equal("acme-42", headers["X-Trunk-Id"]);
        Assert.Equal("campaign-7", headers["X-Session-Tag"]);
    }

    [Fact]
    public void Protected_headers_cannot_be_overridden_by_custom_headers()
    {
        var headers = BuildInviteHeaders(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP evil.example:5060;branch=hijack",
            ["Contact"] = "<sip:attacker@evil.example>",
            ["Call-ID"] = "forged-call-id",
            ["CSeq"] = "99 INVITE"
        });

        Assert.DoesNotContain("evil.example", headers["Via"]);
        Assert.DoesNotContain("attacker", headers["Contact"]);
        Assert.NotEqual("forged-call-id", headers["Call-ID"]);
        Assert.Equal("1 INVITE", headers["CSeq"]);
    }

    [Fact]
    public void Header_injection_attempts_are_dropped()
    {
        var headers = BuildInviteHeaders(new Dictionary<string, string>
        {
            ["X-Injected"] = "value\r\nEvil-Header: attack",
            ["Bad Name"] = "irrelevant",
            ["X-Colon:Name"] = "irrelevant"
        });

        Assert.False(headers.ContainsKey("X-Injected"));
        Assert.False(headers.ContainsKey("Evil-Header"));
        Assert.False(headers.ContainsKey("Bad Name"));
        Assert.DoesNotContain(headers.Keys, k => k.Contains("Evil-Header", StringComparison.OrdinalIgnoreCase));
    }
}
