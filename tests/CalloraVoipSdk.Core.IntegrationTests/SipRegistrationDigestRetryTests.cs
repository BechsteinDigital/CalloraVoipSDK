using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Digests;

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

    private static string Sha512_256Hex(string input)
    {
        var digest = new Sha512tDigest(256);
        var bytes = Encoding.UTF8.GetBytes(input);
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return Convert.ToHexString(output).ToLowerInvariant();
    }

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

    [Fact]
    public async Task Register_challenged_with_sha512_256_retries_authenticated_and_succeeds()
    {
        // Regression for the RFC 8760 deadlock: the selector picks SHA-512-256 (strongest) and the
        // authenticator can now actually compute it, so the challenged REGISTER retries and succeeds.
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Headers.ContainsKey("Authorization")
                ? Echo(req, 200, "OK")
                : Echo(req, 401, "Unauthorized",
                    ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", algorithm=SHA-512-256")),
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

        var registers = transport.SnapshotRequests().Where(r => r.Method == "REGISTER").ToList();
        Assert.Equal(2, registers.Count);
        var authorization = registers[1].Headers["Authorization"];
        Assert.Equal("SHA-512-256", Param(authorization, "algorithm"));

        // The retry's digest must follow the RFC 8760 SHA-512/256 formula (qop=auth, no -sess).
        var cnonce = Param(authorization, "cnonce");
        var nc = Param(authorization, "nc");
        var uri = Param(authorization, "uri");
        var ha1 = Sha512_256Hex($"bob:{Realm}:zanzibar");
        var ha2 = Sha512_256Hex($"REGISTER:{uri}");
        var expectedSha = Sha512_256Hex($"{ha1}:{Nonce}:{nc}:{cnonce}:auth:{ha2}");
        Assert.Equal(expectedSha, Param(authorization, "response"));
    }

    [Fact]
    public async Task Register_offered_md5_and_sha512_256_selects_sha512_256_and_succeeds()
    {
        // RFC 8760 §2.4 multi-challenge — the exact deadlock CF-001 fixes: the registrar offers BOTH
        // MD5 and SHA-512-256 in separate WWW-Authenticate rows. The client must pick the strongest
        // computable one (SHA-512-256) and the authenticated retry must succeed with 200.
        var md5 = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", algorithm=MD5";
        var sha512 = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", algorithm=SHA-512-256";
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Headers.ContainsKey("Authorization")
                ? Echo(req, 200, "OK")
                : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", $"{md5}\n{sha512}")),
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
        var registers = transport.SnapshotRequests().Where(r => r.Method == "REGISTER").ToList();
        Assert.Equal(2, registers.Count);
        // Picked SHA-512-256 over the co-offered MD5.
        Assert.Equal("SHA-512-256", Param(registers[1].Headers["Authorization"], "algorithm"));
    }

    [Fact]
    public async Task Register_stale_nonce_retries_with_the_fresh_nonce_and_succeeds()
    {
        const string FreshNonce = "fresh456nonce";
        var transport = new CapturingSipTransportRuntime
        {
            // Unauthenticated → challenge (nonce1); authenticated with nonce1 → 401 stale=true
            // (fresh nonce); authenticated with the fresh nonce → 200.
            ResponseFactory = req =>
            {
                if (!req.Headers.TryGetValue("Authorization", out var auth))
                    return Echo(req, 401, "Unauthorized",
                        ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\""));

                return Param(auth, "nonce") == Nonce
                    ? Echo(req, 401, "Unauthorized",
                        ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{FreshNonce}\", qop=\"auth\", stale=true"))
                    : Echo(req, 200, "OK");
            },
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

        // Three REGISTERs: challenged, stale-rejected, then accepted with the fresh nonce.
        var registers = transport.SnapshotRequests().Where(r => r.Method == "REGISTER").ToList();
        Assert.Equal(3, registers.Count);
        // The stale retry reused the credentials (no re-prompt) with the server's fresh nonce.
        Assert.Equal(FreshNonce, Param(registers[2].Headers["Authorization"], "nonce"));
        Assert.Equal("bob", Param(registers[2].Headers["Authorization"], "username"));
    }

    [Fact]
    public async Task Register_repeated_stale_nonce_gives_up_without_looping()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // A misbehaving registrar answers stale=true to every authenticated REGISTER: the
            // client must stop after a bounded number of retries rather than loop forever.
            ResponseFactory = req => req.Headers.ContainsKey("Authorization")
                ? Echo(req, 401, "Unauthorized",
                    ("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", stale=true"))
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

        // It ultimately fails (never accepted) rather than looping forever.
        await Assert.ThrowsAsync<SipRegistrationFailedException>(() => service.RegisterAsync(request));

        // Bounded: unauthenticated attempt + one auth retry + at most the stale-retry cap.
        var registers = transport.SnapshotRequests().Where(r => r.Method == "REGISTER").ToList();
        Assert.True(registers.Count <= 4, $"expected a bounded REGISTER count, got {registers.Count}");
    }
}
