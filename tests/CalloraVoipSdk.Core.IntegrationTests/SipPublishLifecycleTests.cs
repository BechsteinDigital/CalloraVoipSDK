using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SIP PUBLISH lifecycle (RFC 3903 §4/§6, CF-066b Slice 3): refresh, modify and remove an existing
/// publication by its entity-tag (SIP-If-Match). A bodyless update (refresh/remove) carries no
/// Content-Type; a 412 Conditional Request Failed is surfaced to the caller.
/// </summary>
public sealed class SipPublishLifecycleTests
{
    private static SipCallSignalingService Build(CapturingSipTransportRuntime transport) =>
        new(transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

    private static SipPublishRequest Update(string? ifMatch, string body, int expires) => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        RemoteUri = "sip:alice@example.test",
        EventType = "presence",
        Body = body,
        ContentType = "application/pidf+xml",
        ExpiresSeconds = expires,
        IfMatch = ifMatch,
    };

    [Fact]
    public async Task Refresh_sends_if_match_and_no_content_type()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH" ? Ok(req, etag: "etag-new", expires: 3600) : null,
        };
        using var service = Build(transport);

        var result = await service.PublishAsync(Update(ifMatch: "etag-old", body: "", expires: 3600));

        Assert.Equal("etag-new", result.ETag);
        var sent = transport.SnapshotRequests().Single(r => r.Method == "PUBLISH");
        Assert.Equal("etag-old", sent.Headers["SIP-If-Match"]);
        Assert.False(sent.Headers.ContainsKey("Content-Type")); // RFC 3903 §6: no body → no Content-Type
        Assert.Equal("0", sent.Headers["Content-Length"]);
        Assert.Equal("3600", sent.Headers["Expires"]);
    }

    [Fact]
    public async Task Remove_sends_if_match_with_expires_zero_and_no_body()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH" ? Ok(req, etag: null, expires: 0) : null,
        };
        using var service = Build(transport);

        var result = await service.PublishAsync(Update(ifMatch: "etag-old", body: "", expires: 0));

        Assert.Equal(200, result.StatusCode);
        var sent = transport.SnapshotRequests().Single(r => r.Method == "PUBLISH");
        Assert.Equal("etag-old", sent.Headers["SIP-If-Match"]);
        Assert.Equal("0", sent.Headers["Expires"]);
        Assert.False(sent.Headers.ContainsKey("Content-Type"));
        Assert.Equal(string.Empty, sent.Body ?? string.Empty);
    }

    [Fact]
    public async Task Modify_sends_if_match_with_the_new_body_and_content_type()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH" ? Ok(req, etag: "etag-2", expires: 3600) : null,
        };
        using var service = Build(transport);

        var result = await service.PublishAsync(Update(ifMatch: "etag-old", body: "<presence/>", expires: 3600));

        Assert.Equal("etag-2", result.ETag);
        var sent = transport.SnapshotRequests().Single(r => r.Method == "PUBLISH");
        Assert.Equal("etag-old", sent.Headers["SIP-If-Match"]);
        Assert.Equal("application/pidf+xml", sent.Headers["Content-Type"]);
        Assert.Equal("<presence/>", sent.Body);
    }

    [Fact]
    public async Task A_412_conditional_request_failure_is_surfaced()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH"
                ? new SipResponse(412, "Conditional Request Failed", BaseHeaders(req), string.Empty)
                : null,
        };
        using var service = Build(transport);

        var result = await service.PublishAsync(Update(ifMatch: "stale-etag", body: "", expires: 3600));

        Assert.Equal(412, result.StatusCode);
        Assert.Null(result.ETag);
    }

    private static SipResponse Ok(CapturedSipRequest request, string? etag, int expires)
    {
        var headers = BaseHeaders(request);
        if (etag is not null)
            headers["SIP-ETag"] = etag;
        headers["Expires"] = expires.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static Dictionary<string, string> BaseHeaders(CapturedSipRequest request)
    {
        var toHeader = request.Headers["To"];
        if (SipProtocol.ExtractTag(toHeader) is null)
            toHeader = $"{toHeader};tag=remote-tag";
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = toHeader,
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
        };
    }
}
