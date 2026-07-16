using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behavioural gate for the outbound INVITE digest-retry loop against a stale-nonce abuse
/// (HARD-A2). A peer that answers every authenticated INVITE with <c>stale=true</c> must not
/// spin <see cref="SipCallSessionTransactionService.SendInviteTransactionAsync"/> into an
/// unbounded INVITE flood; the loop caps stale refreshes and then fails the call.
/// </summary>
public sealed class SipInviteDigestRetryDosTests
{
    private const string Realm = "pbx.example.test";
    private const string Nonce = "n0nce-value";

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
    public async Task Repeated_stale_nonce_invite_gives_up_without_flooding()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // A hostile/misconfigured peer answers stale=true to every authenticated INVITE.
            // Unauthenticated → plain challenge; authenticated → 401 stale=true forever.
            ResponseFactory = req => req.Method != "INVITE"
                ? null
                : req.Headers.ContainsKey("Authorization")
                    ? Echo(req, 401, "Unauthorized",
                        ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", stale=true"))
                    : Echo(req, 401, "Unauthorized",
                        ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\"")),
        };
        var context = new AckTestSipCallSessionContext(transport)
        {
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
        };
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        // Must terminate (throw a final-response failure) rather than loop forever.
        var invite = service.SendInviteTransactionAsync(
            body: null,
            allowRingingTransition: true,
            successState: SipDialogState.Established,
            CancellationToken.None);
        var completed = await Task.WhenAny(invite, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        Assert.True(completed == invite, "INVITE digest-retry loop did not terminate within the timeout window");
        await Assert.ThrowsAsync<SipFinalResponseException>(() => invite).ConfigureAwait(false);

        // Bounded: initial INVITE + first authenticated retry + at most the stale-retry cap (2).
        var invites = transport.SnapshotRequests().Count(r => r.Method == "INVITE");
        Assert.True(invites <= 4, $"expected a bounded INVITE count, got {invites}");
    }
}
