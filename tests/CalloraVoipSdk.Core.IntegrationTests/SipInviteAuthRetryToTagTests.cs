using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for F006 (RFC 3261 §12.1.2). A 401 challenge to an initial INVITE carries a To-tag
/// (the UAS needs it for the auto-ACK), but a non-2xx response does NOT establish a dialog — so that
/// tag MUST NOT be echoed into the authenticated retry INVITE, which is still dialog-establishing and
/// whose To must therefore be tag-less. Previously the transaction service adopted the challenge's
/// To-tag, so the retry INVITE looked in-dialog and a strict UAS (Asterisk) answered
/// 481 Call/Transaction Does Not Exist — blocking every authenticated outbound call.
/// </summary>
public sealed class SipInviteAuthRetryToTagTests
{
    private const string Realm = "pbx.example.test";
    private const string Nonce = "n0nce-value";
    private const string UasToTag = "uas-challenge-tag-9f2a";

    [Fact]
    public async Task Authenticated_retry_invite_carries_a_tagless_To()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Model a real registrar: challenge the unauthenticated INVITE with a 401 that (correctly)
            // adds a To-tag; reject the authenticated retry with a real final response (as if it had
            // reached the dialplan). The point under test is the retry INVITE's To header.
            ResponseFactory = req =>
            {
                if (req.Method != "INVITE")
                    return null;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Via"] = req.Headers["Via"],
                    ["From"] = req.Headers["From"],
                    ["To"] = $"{req.Headers["To"]};tag={UasToTag}",
                    ["Call-ID"] = req.Headers["Call-ID"],
                    ["CSeq"] = req.Headers["CSeq"],
                    ["Contact"] = "<sip:bob@192.0.2.10:5060>",
                };

                return req.Headers.ContainsKey("Authorization")
                    ? new SipResponse(486, "Busy Here", headers, string.Empty)
                    : Challenge(headers);
            },
        };
        var context = new AckTestSipCallSessionContext(transport)
        {
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
        };
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        await Assert.ThrowsAsync<SipFinalResponseException>(() =>
            service.SendInviteTransactionAsync(
                body: null,
                allowRingingTransition: true,
                successState: SipDialogState.Established,
                CancellationToken.None)).ConfigureAwait(false);

        var invites = transport.SnapshotRequests().Where(r => r.Method == "INVITE").ToList();
        Assert.Equal(2, invites.Count); // initial INVITE + authenticated retry

        var retry = invites[1];
        Assert.True(retry.Headers.ContainsKey("Authorization"), "second INVITE must be the authenticated retry.");
        Assert.DoesNotContain(";tag=", retry.Headers["To"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UasToTag, retry.Headers["To"], StringComparison.Ordinal);
    }

    private static SipResponse Challenge(Dictionary<string, string> headers)
    {
        headers["WWW-Authenticate"] = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\"";
        return new SipResponse(401, "Unauthorized", headers, string.Empty);
    }
}
