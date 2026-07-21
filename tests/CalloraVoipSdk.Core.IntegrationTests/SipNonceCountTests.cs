using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-042 (RFC 7616 §3.4): the Digest nonce-count (nc) is coupled to the nonce — <c>1</c> for the first use of a
/// nonce, incrementing only while the same nonce is reused, and reset to <c>1</c> when the server issues a new
/// nonce (a fresh or stale-refreshed challenge). Feeding an ever-incrementing nc across new nonces makes a
/// strict server reject the retry.
/// </summary>
public sealed class SipNonceCountTests
{
    private static string Challenge(string nonce) => $"Digest realm=\"pbx\", nonce=\"{nonce}\", qop=\"auth\"";

    // ── SipNonceCounter unit behaviour ───────────────────────────────────────────

    [Fact]
    public void First_use_of_a_nonce_is_count_one()
    {
        Assert.Equal(1, new SipNonceCounter().NextFor(Challenge("n1")));
    }

    [Fact]
    public void Reusing_the_same_nonce_increments_the_count()
    {
        var counter = new SipNonceCounter();

        Assert.Equal(1, counter.NextFor(Challenge("n1")));
        Assert.Equal(2, counter.NextFor(Challenge("n1")));
        Assert.Equal(3, counter.NextFor(Challenge("n1")));
    }

    [Fact]
    public void A_new_nonce_resets_the_count_to_one()
    {
        var counter = new SipNonceCounter();

        Assert.Equal(1, counter.NextFor(Challenge("n1")));
        Assert.Equal(2, counter.NextFor(Challenge("n1")));
        Assert.Equal(1, counter.NextFor(Challenge("n2"))); // new nonce → reset
        Assert.Equal(1, counter.NextFor(Challenge("n1"))); // a returning nonce is treated as fresh too
    }

    [Fact]
    public void The_nonce_is_read_through_lws_and_quotes()
    {
        var counter = new SipNonceCounter();

        Assert.Equal(1, counter.NextFor("Digest realm=\"pbx\",  nonce = \"abc\" , qop=\"auth\""));
        Assert.Equal(2, counter.NextFor("Digest nonce=\"abc\", realm=\"pbx\"")); // same nonce, different order
    }

    // ── CF-042 end to end through the outbound INVITE digest retry ───────────────

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
    public async Task A_stale_challenge_with_a_new_nonce_retries_with_nc_reset_to_one()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req =>
            {
                if (req.Method != "INVITE")
                    return null;
                if (!req.Headers.TryGetValue("Authorization", out var auth))
                    return Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("nonceA")));
                // Authenticated with nonceA → issue a NEW nonce (stale refresh); authenticated with nonceB → accept.
                return auth.Contains("nonce=\"nonceA\"", StringComparison.Ordinal)
                    ? Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("nonceB") + ", stale=true"))
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

        Assert.Equal(2, authedInvites.Count);
        Assert.Contains("nonce=\"nonceA\"", authedInvites[0], StringComparison.Ordinal);
        Assert.Contains("nc=00000001", authedInvites[0], StringComparison.Ordinal);
        Assert.Contains("nonce=\"nonceB\"", authedInvites[1], StringComparison.Ordinal);
        Assert.Contains("nc=00000001", authedInvites[1], StringComparison.Ordinal); // reset, not 00000002
    }
}
