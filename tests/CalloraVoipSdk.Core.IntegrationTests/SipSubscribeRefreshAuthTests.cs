using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #4, part 2/2 — the SUBSCRIBE refresh path (RFC 6665 §4.1.2 / RFC 3261 §22):
/// <list type="bullet">
/// <item><description>a 401/407 challenge on the refresh is answered with digest and retried (else the
/// subscription silently lapses behind an authenticating proxy);</description></item>
/// <item><description>the server-granted lease is honoured as-is — a sub-60s grant is NOT raised to 60s (which
/// would schedule the refresh after the real lease expired);</description></item>
/// <item><description>a non-positive lease stops the refresh loop instead of busy-looping on a zero delay.</description></item>
/// </list>
/// </summary>
public sealed class SipSubscribeRefreshAuthTests
{
    private static string Challenge(string nonce) => $"Digest realm=\"pbx\", nonce=\"{nonce}\", qop=\"auth\"";

    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, params (string, string)[] extra)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = EnsureToTag(req.Headers["To"]),
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
        };
        foreach (var (name, value) in extra)
            headers[name] = value;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    private static string EnsureToTag(string toHeader) =>
        SipProtocol.ExtractTag(toHeader) is not null ? toHeader : $"{toHeader};tag=remote-tag";

    private static SipCallSignalingSubscriptions BuildSubscriptions(CapturingSipTransportRuntime transport) =>
        new(
            transport,
            new SipDigestAuthentication(),
            new SipClientTransactionExecutor(transport, NullLogger.Instance),
            new ConcurrentDictionary<string, SipOutboundSubscriptionEntry>(),
            NullLogger.Instance,
            (_, _, _, _, _, _) => Task.CompletedTask);

    private static SipSubscribeRequest Request(int expiresSeconds, string? password) => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.test",
        RemoteUri = "sip:bob@example.test",
        EventType = "presence",
        ExpiresSeconds = expiresSeconds,
        AuthPassword = password,
    };

    [Fact]
    public async Task A_short_lease_refresh_authenticates_and_honours_the_granted_expires()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req =>
            {
                if (req.Method != "SUBSCRIBE")
                    return null;
                // Grant a short 1 s lease so the refresh fires within the test window; challenge an unauthenticated
                // refresh with 401 so the digest retry is exercised, and honour Expires: 1 on every accept.
                if (req.Headers.ContainsKey("Authorization"))
                    return Echo(req, 200, "OK", ("Expires", "1"));
                var cseq = req.Headers.TryGetValue("CSeq", out var c) ? c : string.Empty;
                return cseq.StartsWith("1 ", StringComparison.Ordinal)
                    ? Echo(req, 200, "OK", ("Expires", "1"))                                   // initial → grant 1 s
                    : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1")));   // refresh → challenge
            },
        };
        var subscriptions = BuildSubscriptions(transport);

        var handle = await subscriptions.SubscribeAsync(Request(expiresSeconds: 1, password: "s3cret"));
        // The refresh fires at ~1 s for a 1 s lease; give it room to run once, then stop the loop.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await handle.DisposeAsync();

        // A challenged refresh was re-sent WITH digest, carrying the honoured 1 s lease. Before the fix the 1 s
        // grant was raised to 60 s → the refresh only fires at ~54 s → nothing authenticated within the window.
        Assert.Contains(
            transport.SnapshotRequests(),
            r => r.Method == "SUBSCRIBE"
                 && r.Headers.ContainsKey("Authorization")
                 && r.Headers.TryGetValue("Expires", out var e) && e == "1");
    }

    [Fact]
    public async Task A_zero_expires_subscription_does_not_busy_loop_refreshing()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Accept every SUBSCRIBE with no Expires header → the negotiated lease is 0.
            ResponseFactory = req => req.Method == "SUBSCRIBE" ? Echo(req, 200, "OK") : null,
        };
        var subscriptions = BuildSubscriptions(transport);

        var handle = await subscriptions.SubscribeAsync(Request(expiresSeconds: 0, password: null));
        await Task.Delay(TimeSpan.FromMilliseconds(300)); // a zero-delay busy-loop would emit hundreds of SUBSCRIBEs
        await handle.DisposeAsync();

        // The refresh loop stopped on the non-positive lease instead of spinning: only the initial SUBSCRIBE
        // (and at most the dispose unsubscribe) was sent.
        var subscribes = transport.SnapshotRequests().Count(r => r.Method == "SUBSCRIBE");
        Assert.True(subscribes <= 3, $"expected no refresh busy-loop, got {subscribes} SUBSCRIBEs");
    }
}
