using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-1: a <c>416 Unsupported URI Scheme</c> on a <c>sips:</c> target must NOT be auto-retried over a
/// downgraded <c>sip:</c> URI. Silently downgrading would let a peer or proxy strip the caller's end-to-end SIPS
/// security intent down to a cleartext hop by answering 416. The 416 must propagate as a final failure and the
/// original sips: request URI must never be rewritten to sip:.
/// </summary>
public sealed class SipSipsDowngradeTests
{
    [Fact]
    public async Task A_416_on_a_sips_invite_target_does_not_downgrade_to_sip()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance);

        // Every INVITE is answered 416 — a downgrading client would retry a second INVITE over sip:.
        transport.ResponseFactory = request =>
            request.Method.Equals("INVITE", StringComparison.Ordinal)
                ? CreateResponse(request, 416, "Unsupported URI Scheme")
                : null;

        var invite = new SipInviteRequest
        {
            LocalUsername = "alice",
            LocalDomain = "example.com",
            RemoteUri = "sips:bob@192.0.2.10",
            SessionDescription = "v=0\r\n",
            Transport = SipTransportProtocol.Tls, // sips: requires a secure transport, else the target is skipped
            Timeout = TimeSpan.FromSeconds(5),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => service.InviteAsync(invite));

        // Exactly one INVITE — no downgraded sip: retry — and its request URI stayed sips:.
        var invites = transport.SnapshotRequests().Where(r => r.Method == "INVITE").ToList();
        var single = Assert.Single(invites);
        Assert.StartsWith("sips:", single.RequestUri);
    }

    private static SipResponse CreateResponse(CapturedSipRequest request, int statusCode, string reasonPhrase)
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
            ["Contact"] = "<sips:bob@192.0.2.10:5061>",
        };

        return new SipResponse(statusCode, reasonPhrase, headers, string.Empty);
    }
}
