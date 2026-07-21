using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-047 + Digest (RFC 7616 §3.4): a 422 "Session Interval Too Small" retry of an already-authenticated INVITE
/// must carry a fresh, incrementing nonce-count (nc) — not a replay of the finished Authorization line from the
/// first authenticated attempt. Replaying the same nonce+nc is exactly what a strict, replay-tracking server
/// rejects. The loop stores the selected challenge and rebuilds the Authorization per request, so each send
/// advances the nc.
/// </summary>
public sealed class SipInviteAuth422ReplayTests
{
    private static string Challenge(string nonce) => $"Digest realm=\"pbx\", nonce=\"{nonce}\", qop=\"auth\"";

    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, (string, string)? extra = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
            ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:bob@192.0.2.10:5060>",
        };
        if (extra is { } e)
            headers[e.Item1] = e.Item2;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    [Fact]
    public async Task A_422_retry_after_authentication_increments_the_nonce_count_instead_of_replaying_it()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req =>
            {
                if (req.Method != "INVITE")
                    return null;
                // Unauthenticated → challenge. Authenticated → 422 for the default 1800 offer, 200 for the raised.
                if (!req.Headers.ContainsKey("Authorization"))
                    return Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1")));
                var se = req.Headers.TryGetValue("Session-Expires", out var s) ? s : string.Empty;
                return se.StartsWith("1800", StringComparison.Ordinal)
                    ? Echo(req, 422, "Session Interval Too Small", ("Min-SE", "3600"))
                    : Echo(req, 200, "OK");
            },
        };
        var context = new AckTestSipCallSessionContext(transport)
        {
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
        };
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        var authedInvites = transport.SnapshotRequests()
            .Where(r => r.Method == "INVITE" && r.Headers.ContainsKey("Authorization"))
            .Select(r => r.Headers["Authorization"])
            .ToList();

        Assert.Equal(2, authedInvites.Count); // the first authenticated attempt (422) and the raised retry (200)
        Assert.Contains("nonce=\"n1\"", authedInvites[0], StringComparison.Ordinal);
        Assert.Contains("nc=00000001", authedInvites[0], StringComparison.Ordinal);
        Assert.Contains("nonce=\"n1\"", authedInvites[1], StringComparison.Ordinal); // same nonce reused…
        Assert.Contains("nc=00000002", authedInvites[1], StringComparison.Ordinal); // …but nc advanced, not replayed
    }
}
