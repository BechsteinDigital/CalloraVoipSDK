using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end REGISTER digest flow with the real <see cref="SipDigestAuthentication"/>:
/// unauthenticated REGISTER → 401 challenge → authenticated retry → 200. Until now this path
/// was only ever validated by a live registrar call; here the registrar is a canned transport
/// that challenges once and accepts the digest, and the emitted Authorization is verified
/// against the RFC 2617 formula.
/// </summary>
public sealed class SipRegistrationDigestRetryTests
{
    private const string Realm = "pbx.example.com";
    private const string Nonce = "abc123nonce";

    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, (string, string)? extra = null)
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
        if (extra is { } e)
            headers[e.Item1] = e.Item2;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    private static string HashHexLower(string input, HashAlgorithm algorithm) =>
        Convert.ToHexString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string? Param(string header, string name)
    {
        var match = Regex.Match(header, name + @"=(?:""([^""]*)""|([^,\s]+))");
        return !match.Success ? null : match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    [Fact]
    public async Task Register_challenged_with_401_retries_authenticated_and_succeeds()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Challenge the first (unauthenticated) REGISTER, accept the authenticated retry.
            ResponseFactory = req => req.Headers.ContainsKey("Authorization")
                ? Echo(req, 200, "OK")
                : Echo(req, 401, "Unauthorized",
                    ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\"")),
        };
        var service = new SipRegistrationService(
            transport, new SipDigestAuthentication(), NullLoggerFactory.Instance);

        var request = new SipRegistrationRequest
        {
            Username = "bob",
            Password = "zanzibar",
            Domain = Realm,
            Port = 5060,
            Timeout = TimeSpan.FromSeconds(2),
        };

        var result = await service.RegisterAsync(request);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Authenticated);

        // Exactly two REGISTERs: the challenged one, then the authenticated retry.
        var registers = transport.SnapshotRequests().Where(r => r.Method == "REGISTER").ToList();
        Assert.Equal(2, registers.Count);
        Assert.False(registers[0].Headers.ContainsKey("Authorization"));

        // The retry's Authorization must carry a digest computed with the RFC 2617 formula.
        var authorization = registers[1].Headers["Authorization"];
        Assert.Equal(Realm, Param(authorization, "realm"));
        Assert.Equal(Nonce, Param(authorization, "nonce"));
        Assert.Equal("auth", Param(authorization, "qop"));
        var cnonce = Param(authorization, "cnonce");
        var nc = Param(authorization, "nc");
        var uri = Param(authorization, "uri");

        using var md5 = MD5.Create();
        var ha1 = HashHexLower($"bob:{Realm}:zanzibar", md5);
        var ha2 = HashHexLower($"REGISTER:{uri}", md5);
        var expected = HashHexLower($"{ha1}:{Nonce}:{nc}:{cnonce}:auth:{ha2}", md5);
        Assert.Equal(expected, Param(authorization, "response"));
    }
}
