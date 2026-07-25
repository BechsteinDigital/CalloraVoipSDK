using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound out-of-dialog SIP PUBLISH (RFC 3903, CF-066b): the signaling service sends a PUBLISH carrying
/// the Event/Expires/body, surfaces the SIP-ETag + granted Expires from a 2xx, and answers a 401/407
/// challenge with long-term digest credentials (RFC 3261 §22) before retrying.
/// </summary>
public sealed class SipPublishOutboundTests
{
    private static SipCallSignalingService Build(CapturingSipTransportRuntime transport, ISipDigestAuthenticator? auth = null) =>
        new(transport, auth ?? new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

    private static SipPublishRequest Request(string? password = null) => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        AuthPassword = password,
        RemoteUri = "sip:alice@example.test",
        EventType = "presence",
        Body = "<presence/>",
        ContentType = "application/pidf+xml",
        ExpiresSeconds = 3600,
    };

    [Fact]
    public async Task Sends_a_PUBLISH_and_surfaces_the_etag_and_expires()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH" ? Ok(req, etag: "etag-42", expires: 1800) : null,
        };
        using var service = Build(transport);

        var result = await service.PublishAsync(Request());

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("etag-42", result.ETag);
        Assert.Equal(1800, result.ExpiresSeconds);

        var sent = transport.SnapshotRequests().Single(r => r.Method == "PUBLISH");
        Assert.Equal("sip:alice@example.test", sent.RequestUri);
        Assert.Equal("presence", sent.Headers["Event"]);
        Assert.Equal("3600", sent.Headers["Expires"]);
        Assert.Equal("application/pidf+xml", sent.Headers["Content-Type"]);
        Assert.Equal("<presence/>", sent.Body);
        Assert.Equal("1 PUBLISH", sent.Headers["CSeq"]);
    }

    [Fact]
    public async Task Answers_a_407_challenge_with_digest_credentials_and_retries()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req =>
            {
                if (req.Method != "PUBLISH")
                    return null;
                return req.Headers.ContainsKey("Proxy-Authorization") ? Ok(req, etag: "etag-auth", expires: 3600) : Challenge(req);
            },
        };
        using var service = Build(transport, new SipDigestAuthentication());

        var result = await service.PublishAsync(Request(password: "s3cr3t"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("etag-auth", result.ETag);
        var sent = transport.SnapshotRequests().Where(r => r.Method == "PUBLISH").ToList();
        Assert.Equal(2, sent.Count);
        Assert.False(sent[0].Headers.ContainsKey("Proxy-Authorization"));
        Assert.True(sent[1].Headers.ContainsKey("Proxy-Authorization")); // authenticated retry
        Assert.Equal("2 PUBLISH", sent[1].Headers["CSeq"]);              // CSeq incremented for the retry
    }

    private static SipResponse Ok(CapturedSipRequest request, string etag, int expires)
    {
        var headers = BaseHeaders(request, challenge: false);
        headers["SIP-ETag"] = etag;
        headers["Expires"] = expires.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static SipResponse Challenge(CapturedSipRequest request) =>
        new(407, "Proxy Authentication Required", BaseHeaders(request, challenge: true), string.Empty);

    private static Dictionary<string, string> BaseHeaders(CapturedSipRequest request, bool challenge)
    {
        var toHeader = request.Headers["To"];
        if (SipProtocol.ExtractTag(toHeader) is null)
            toHeader = $"{toHeader};tag=remote-tag";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = toHeader,
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
        };
        if (challenge)
            headers["Proxy-Authenticate"] = "Digest realm=\"example.com\", nonce=\"abc123nonce\", algorithm=MD5, qop=\"auth\"";
        return headers;
    }
}
