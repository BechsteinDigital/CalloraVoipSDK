using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound out-of-dialog SIP MESSAGE (RFC 3428, CF-066a Slice 3): the signaling service sends a MESSAGE
/// carrying the target/body/content-type, and answers a 407 challenge with long-term digest credentials
/// (RFC 3261 §22) before retrying.
/// </summary>
public sealed class SipMessageOutboundTests
{
    private static SipCallSignalingService Build(CapturingSipTransportRuntime transport, ISipDigestAuthenticator? auth = null) =>
        new(transport, auth ?? new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

    private static SipMessageRequest Request(string? password = null) => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        AuthPassword = password,
        RemoteUri = "sip:bob@example.test",
        Body = "hi there",
        ContentType = "text/plain",
    };

    [Fact]
    public async Task Sends_a_MESSAGE_with_the_target_body_and_content_type()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "MESSAGE" ? Ok(req) : null,
        };
        using var service = Build(transport);

        var status = await service.SendMessageAsync(Request());

        Assert.Equal(200, status);
        var sent = transport.SnapshotRequests().Single(r => r.Method == "MESSAGE");
        Assert.Equal("sip:bob@example.test", sent.RequestUri);
        Assert.Equal("hi there", sent.Body);
        Assert.Equal("text/plain", sent.Headers["Content-Type"]);
        Assert.Equal("1 MESSAGE", sent.Headers["CSeq"]);
    }

    [Fact]
    public async Task Answers_a_407_challenge_with_digest_credentials_and_retries()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req =>
            {
                if (req.Method != "MESSAGE")
                    return null;
                return req.Headers.ContainsKey("Proxy-Authorization") ? Ok(req) : Challenge(req);
            },
        };
        using var service = Build(transport, new SipDigestAuthentication());

        var status = await service.SendMessageAsync(Request(password: "s3cr3t"));

        Assert.Equal(200, status);
        var sent = transport.SnapshotRequests().Where(r => r.Method == "MESSAGE").ToList();
        Assert.Equal(2, sent.Count);
        Assert.False(sent[0].Headers.ContainsKey("Proxy-Authorization"));
        Assert.True(sent[1].Headers.ContainsKey("Proxy-Authorization")); // authenticated retry
        Assert.Equal("2 MESSAGE", sent[1].Headers["CSeq"]);              // CSeq incremented for the retry
    }

    private static SipResponse Ok(CapturedSipRequest request) => Build(request, 200, "OK", challenge: false);

    private static SipResponse Challenge(CapturedSipRequest request) =>
        Build(request, 407, "Proxy Authentication Required", challenge: true);

    private static SipResponse Build(CapturedSipRequest request, int statusCode, string reasonPhrase, bool challenge)
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

        return new SipResponse(statusCode, reasonPhrase, headers, string.Empty);
    }
}
