using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-014 + Digest (RFC 3261 §22.4 / RFC 2617): the Digest <c>uri=</c>/A2 must sign the <em>effective</em>
/// Request-URI that actually goes on the wire. On a strict-router in-dialog request the wire Request-URI is the
/// topmost route (not the dialog remote target), so the routing plan — computed before authentication — decides
/// the digest URI. Signing the remote target instead makes the server recompute over a different URI and reject
/// the retry. A loose/direct dialog keeps the remote target as the digest URI (unchanged behaviour).
/// </summary>
public sealed class SipInDialogDigestStrictRouteTests
{
    private const string RemoteTarget = "sip:bob@example.test"; // AckTestSipCallSessionContext default RemoteRequestUri
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

    private static CapturingSipTransportRuntime ChallengeThenAcceptInfo() =>
        new()
        {
            ResponseFactory = req =>
            {
                if (req.Method != "INFO")
                    return null;
                return req.Headers.ContainsKey("Authorization")
                    ? Echo(req, 200, "OK")
                    : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1")));
            },
        };

    [Fact]
    public async Task A_strict_routed_in_dialog_info_signs_the_effective_request_uri_not_the_remote_target()
    {
        var transport = ChallengeThenAcceptInfo();
        var context = new AckTestSipCallSessionContext(transport)
        {
            RemoteTag = "remote-tag",
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
            // Topmost route has no ;lr → strict router: the wire Request-URI becomes this route (RFC 3261 §12.2.1.1).
            DialogRouteSet = ["sip:strict.example.net:6001", "sip:proxy2.example.net:6002;lr"],
        };
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInfoAsync("application/dtmf-relay", "Signal=1", CancellationToken.None);

        var authed = transport.SnapshotRequests()
            .Single(r => r.Method == "INFO" && r.Headers.ContainsKey("Authorization"));
        // The digest signs the strict-router Request-URI — and it matches the wire Request-URI the server sees.
        Assert.Equal("sip:strict.example.net:6001", authed.RequestUri);
        Assert.Contains(
            "uri=\"sip:strict.example.net:6001\"", authed.Headers["Authorization"], StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"uri=\"{RemoteTarget}\"", authed.Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_loose_routed_in_dialog_info_signs_the_remote_target_unchanged()
    {
        var transport = ChallengeThenAcceptInfo();
        var context = new AckTestSipCallSessionContext(transport)
        {
            RemoteTag = "remote-tag",
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
            DialogRouteSet = ["sip:proxy1.example.net:6001;lr"], // loose router keeps the remote target as R-URI
        };
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInfoAsync("application/dtmf-relay", "Signal=1", CancellationToken.None);

        var authed = transport.SnapshotRequests()
            .Single(r => r.Method == "INFO" && r.Headers.ContainsKey("Authorization"));
        Assert.Equal(RemoteTarget, authed.RequestUri);
        Assert.Contains($"uri=\"{RemoteTarget}\"", authed.Headers["Authorization"], StringComparison.Ordinal);
    }
}
