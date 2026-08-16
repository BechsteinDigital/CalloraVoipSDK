using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #158 P2-13 — a REGISTER redirect used to carry the Authorization header onto the next target. A Digest
/// response is computed over realm, nonce, method and URI (RFC 7616 §3.4): it is worthless to a different
/// authority and hands it our nonce, nc, cnonce and the response hash for free. The redirect chain was also
/// bounded only by duplicate suppression, which stops cycles but not length.
///
/// <para>
/// pjsip avoids this structurally by holding credentials in a realm-aware <c>pjsip_auth_clt_sess</c> rather
/// than as a raw header string; we carry the header, so it has to be dropped explicitly.
/// </para>
/// </summary>
public sealed class SipRegisterRedirectCredentialTests
{
    private const string Realm = "registrar.example";
    private const string Challenge = "Digest realm=\"registrar.example\", nonce=\"abc123\"";

    private static SipRegistrationRequest Request() => new()
    {
        Username = "alice",
        Password = "secret",
        Domain = Realm,
        Port = 5060,
        Transport = SipTransportProtocol.Udp,
        Timeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public async Task The_authorization_header_does_not_follow_a_redirect()
    {
        // 401 → we authenticate → 302 to another authority → the third request must go out clean.
        var executor = new ScriptedExecutor(
            Unauthorized(),
            Redirect("sip:registrar.other.example"),
            Ok());

        var service = NewService(executor);
        await service.RegisterAsync(Request());

        Assert.Equal(3, executor.Requests.Count);
        Assert.True(executor.Requests[1].Headers.ContainsKey("Authorization"),
            "the retry after the challenge must carry the credentials");
        Assert.False(executor.Requests[2].Headers.ContainsKey("Authorization"),
            "the request after the redirect must not carry credentials for the previous authority");
    }

    [Fact]
    public async Task The_redirect_target_is_actually_used()
    {
        // Guards the test above: if the redirect were not followed at all, the missing header would prove
        // nothing.
        var executor = new ScriptedExecutor(
            Redirect("sip:registrar.other.example"),
            Ok());

        var service = NewService(executor);
        await service.RegisterAsync(Request());

        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains("registrar.other.example", executor.Requests[1].RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_chain_is_bounded()
    {
        // Every response hands out a fresh target, so duplicate suppression never trips. Without a hop
        // counter this walks forever; the cap is five, so at most six requests leave the client.
        var responses = Enumerable.Range(0, 20)
            .Select(i => Redirect($"sip:hop{i}.example"))
            .ToArray();
        var executor = new ScriptedExecutor(responses);

        var service = NewService(executor);

        // Giving up is the point: the chain never reaches a registrar, so the attempt fails rather than
        // continuing to walk. Without the cap this would not throw — it would keep sending.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(Request()));

        Assert.InRange(executor.Requests.Count, 1, 6);
    }

    [Fact]
    public async Task Without_a_redirect_the_credentials_still_reach_the_registrar()
    {
        // The fix must not disturb the ordinary challenge/retry flow.
        var executor = new ScriptedExecutor(Unauthorized(), Ok());

        var service = NewService(executor);
        var result = await service.RegisterAsync(Request());

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, executor.Requests.Count);
        Assert.True(executor.Requests[1].Headers.ContainsKey("Authorization"));
    }

    // ── scaffolding ──────────────────────────────────────────────────────────

    private static SipRegistrationService NewService(ISipClientTransactionExecutor executor) =>
        new(new CapturingSipTransportRuntime(), new StubAuthenticator(), NullLoggerFactory.Instance, null, executor);

    private static SipResponse Unauthorized() => new(
        401, "Unauthorized",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WWW-Authenticate"] = Challenge },
        string.Empty);

    private static SipResponse Redirect(string contact) => new(
        302, "Moved Temporarily",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Contact"] = $"<{contact}>" },
        string.Empty);

    private static SipResponse Ok() => new(
        200, "OK",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Contact"] = "<sip:alice@host>;expires=300" },
        string.Empty);

    /// <summary>Replays a fixed response sequence and records every request it was asked to send.</summary>
    private sealed class ScriptedExecutor(params SipResponse[] responses) : ISipClientTransactionExecutor
    {
        private int _index;

        public List<SipClientTransactionRequest> Requests { get; } = [];

        public Task<SipClientTransactionResult> ExecuteAsync(
            SipClientTransactionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var response = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new SipClientTransactionResult
            {
                FinalResponse = new SipResponseEnvelope(new IPEndPoint(IPAddress.Loopback, 5060), response),
            });
        }
    }

    /// <summary>Produces a recognisable Authorization header without doing real digest arithmetic.</summary>
    private sealed class StubAuthenticator : ISipDigestAuthenticator
    {
        public bool TryCreateAuthorizationHeader(
            string? challengeHeader, string username, string password, string method, string requestUri,
            int nonceCount, out string authorizationHeader, string? body = null)
        {
            authorizationHeader = $"Digest username=\"{username}\", realm=\"{Realm}\", nonce=\"abc123\"";
            return !string.IsNullOrWhiteSpace(challengeHeader);
        }
    }

}
