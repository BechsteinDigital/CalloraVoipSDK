using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #4 (RFC 4028 + RFC 3261 §22.2): a 401/407 challenge on the in-dialog session-timer refresh UPDATE is
/// answered with digest credentials and the UPDATE is retried, so a session refresh behind an authenticating
/// proxy succeeds instead of being reported as a failed refresh that tears the healthy dialog down via BYE.
/// </summary>
public sealed class SipSessionRefreshAuthTests
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
        };
        if (extra is { } e)
            headers[e.Item1] = e.Item2;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    private static AckTestSipCallSessionContext Context(CapturingSipTransportRuntime transport, bool withPassword) =>
        new(transport)
        {
            RemoteTag = "remote-tag",
            AuthPassword = withPassword ? "s3cret" : null,
            DigestAuthenticator = new SipDigestAuthentication(),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5060),
        };

    [Fact]
    public async Task A_401_on_the_refresh_update_retries_with_digest_and_succeeds()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method != "UPDATE"
                ? null
                : req.Headers.ContainsKey("Authorization")
                    ? Echo(req, 200, "OK", ("Session-Expires", "1800;refresher=uac"))
                    : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1"))),
        };
        var context = Context(transport, withPassword: true);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        var accepted = await service.SendSessionRefreshUpdateAsync(CancellationToken.None);

        // The refresh succeeded → the session-timer manager will NOT tear the dialog down (before the fix a 401
        // returned false immediately after a single UPDATE, dropping the healthy call).
        Assert.True(accepted);
        var updates = transport.SnapshotRequests().Where(r => r.Method == "UPDATE").ToList();
        Assert.Equal(2, updates.Count);
        Assert.False(updates[0].Headers.ContainsKey("Authorization"));
        Assert.True(updates[1].Headers.ContainsKey("Authorization"));
        Assert.Contains("nonce=\"n1\"", updates[1].Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_without_credentials_fails_the_refresh_without_looping()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method != "UPDATE"
                ? null
                : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1"))),
        };
        var context = Context(transport, withPassword: false);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        var accepted = await service.SendSessionRefreshUpdateAsync(CancellationToken.None);

        Assert.False(accepted);
        Assert.Single(transport.SnapshotRequests().Where(r => r.Method == "UPDATE")); // no credentials → no retry
    }
}
