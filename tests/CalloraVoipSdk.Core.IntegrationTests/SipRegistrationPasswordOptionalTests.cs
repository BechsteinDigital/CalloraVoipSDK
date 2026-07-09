using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SIP authentication is challenge-driven (RFC 3261 §22): a password is only needed to answer
/// a 401/407. Registration therefore does not require a password up front — it succeeds
/// without one against a registrar that does not challenge, and only fails (with a clear,
/// specific error) when a challenge arrives and no password is configured.
/// </summary>
public sealed class SipRegistrationPasswordOptionalTests
{
    private static SipRegistrationRequest Request(string password) => new()
    {
        Username = "user",
        Password = password,
        Domain = "pbx.example.com",
        Port = 5060,
        Timeout = TimeSpan.FromSeconds(2),
    };

    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, string? extraKey = null, string? extraValue = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
            ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:user@127.0.0.1:5060>",
        };
        if (extraKey is not null && extraValue is not null)
            headers[extraKey] = extraValue;

        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    [Fact]
    public async Task Register_without_password_succeeds_when_registrar_does_not_challenge()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => Echo(req, 200, "OK"),
        };
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        var result = await service.RegisterAsync(Request(password: string.Empty));

        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Authenticated);
    }

    [Fact]
    public async Task Register_without_password_throws_clear_error_when_challenged()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => Echo(
                req, 401, "Unauthorized",
                "WWW-Authenticate", "Digest realm=\"pbx.example.com\", nonce=\"abc123\""),
        };
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        // Exact type: a plain InvalidOperationException, not the SipRegistrationFailedException
        // subclass — signalling a local misconfiguration rather than a server-driven failure.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterAsync(Request(password: string.Empty)));

        Assert.Contains("no password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_with_password_challenge_does_not_trigger_missing_password_error()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => Echo(
                req, 401, "Unauthorized",
                "WWW-Authenticate", "Digest realm=\"pbx.example.com\", nonce=\"abc123\""),
        };
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        // A configured password means the guardrail must NOT fire: an unanswerable challenge
        // surfaces as the normal server-driven registration failure (401), not the
        // missing-password error.
        await Assert.ThrowsAsync<SipRegistrationFailedException>(
            () => service.RegisterAsync(Request(password: "secret")));
    }

    [Fact]
    public void SipAccount_can_be_constructed_without_a_password()
    {
        var account = new SipAccount
        {
            Username = "user",
            SipServer = "pbx.example.com",
        };

        Assert.Equal(string.Empty, account.Password);
        Assert.Equal("user", account.Credentials.Username);
    }
}
