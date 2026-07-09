using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Known-answer and structural coverage for the real SIP Digest authenticator
/// (RFC 2617 / RFC 7616). Until now only the no-op authenticator was exercised, so a wrong
/// digest computation would only surface against a live registrar. The no-qop reference
/// responses are computed independently (md5sum / openssl), not derived from the SUT.
/// </summary>
public sealed class SipDigestAuthenticationTests
{
    private const string Username = "bob";
    private const string Realm = "biloxi.com";
    private const string Password = "zanzibar";
    private const string Method = "REGISTER";
    private const string Uri = "sip:biloxi.com";
    private const string Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";

    private static string? Param(string header, string name)
    {
        var match = Regex.Match(header, name + @"=(?:""([^""]*)""|([^,\s]+))");
        if (!match.Success)
            return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string HashHexLower(string input, HashAlgorithm algorithm)
    {
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashHexLower(string input, string algorithm)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = algorithm switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA-256" => SHA256.HashData(bytes),
            _ => throw new ArgumentException($"Unsupported algorithm {algorithm}", nameof(algorithm)),
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void No_qop_md5_response_matches_the_reference_digest()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\"";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.True(ok);
        // Independently computed: md5(md5(bob:biloxi.com:zanzibar):nonce:md5(REGISTER:sip:biloxi.com)).
        Assert.Equal("4441045a8075db3ead543693997e2a0e", Param(header, "response"));
        Assert.Equal(Username, Param(header, "username"));
        Assert.Equal(Realm, Param(header, "realm"));
        Assert.Equal(Nonce, Param(header, "nonce"));
        Assert.Equal(Uri, Param(header, "uri"));
        Assert.Equal("MD5", Param(header, "algorithm"));
        // No qop offered → no qop/nc/cnonce in the response (RFC 2069 form).
        Assert.DoesNotContain("qop=", header);
        Assert.DoesNotContain("cnonce=", header);
    }

    [Fact]
    public void No_qop_sha256_response_matches_the_reference_digest()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-256";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.True(ok);
        Assert.Equal(
            "10503915e86126c221a99ecec4bd61ed6a5f0c1bac8125b5df9db47ab06503c1",
            Param(header, "response"));
        Assert.Equal("SHA-256", Param(header, "algorithm"));
    }

    [Fact]
    public void Qop_auth_md5_uses_the_rfc2617_formula_with_the_emitted_cnonce()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\"";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 7, out var header);

        Assert.True(ok);
        Assert.Equal("auth", Param(header, "qop"));
        Assert.Equal("00000007", Param(header, "nc"));
        var cnonce = Param(header, "cnonce");
        Assert.False(string.IsNullOrWhiteSpace(cnonce));

        // Recompute the response with the RFC 2617 §3.2.2.1 formula and the cnonce the
        // authenticator actually emitted; a deviation (wrong order/separator) would differ.
        using var md5 = MD5.Create();
        var ha1 = HashHexLower($"{Username}:{Realm}:{Password}", md5);
        var ha2 = HashHexLower($"{Method}:{Uri}", md5);
        var expected = HashHexLower($"{ha1}:{Nonce}:00000007:{cnonce}:auth:{ha2}", md5);
        Assert.Equal(expected, Param(header, "response"));
    }

    [Fact]
    public void Qop_auth_md5_sess_folds_nonce_and_cnonce_into_ha1()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", algorithm=MD5-sess";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.True(ok);
        Assert.Equal("MD5-sess", Param(header, "algorithm"));
        AssertSessResponse(header, "MD5");
    }

    [Fact]
    public void Qop_auth_sha256_sess_folds_nonce_and_cnonce_into_ha1()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth\", algorithm=SHA-256-sess";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.True(ok);
        Assert.Equal("SHA-256-sess", Param(header, "algorithm"));
        AssertSessResponse(header, "SHA-256");
    }

    // RFC 7616 §3.4.2 session variant: HA1 = H(H(user:realm:pass):nonce:cnonce), recomputed here
    // with the cnonce the authenticator emitted, against a qop=auth response.
    private static void AssertSessResponse(string header, string hashAlgorithm)
    {
        var cnonce = Param(header, "cnonce");
        Assert.False(string.IsNullOrWhiteSpace(cnonce));
        Assert.Equal("00000001", Param(header, "nc"));

        var ha1Base = HashHexLower($"{Username}:{Realm}:{Password}", hashAlgorithm);
        var ha1 = HashHexLower($"{ha1Base}:{Nonce}:{cnonce}", hashAlgorithm);
        var ha2 = HashHexLower($"{Method}:{Uri}", hashAlgorithm);
        var expected = HashHexLower($"{ha1}:{Nonce}:00000001:{cnonce}:auth:{ha2}", hashAlgorithm);
        Assert.Equal(expected, Param(header, "response"));
    }

    [Fact]
    public void Opaque_is_echoed_when_the_challenge_carries_it()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", opaque=\"5ccc069c\"";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.True(ok);
        Assert.Equal("5ccc069c", Param(header, "opaque"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic realm=\"biloxi.com\"")]                       // not a Digest challenge
    [InlineData("Digest nonce=\"abc\"")]                              // missing realm
    [InlineData("Digest realm=\"biloxi.com\"")]                      // missing nonce
    [InlineData("Digest realm=\"biloxi.com\", nonce=\"abc\", algorithm=MD6")] // unsupported algorithm
    [InlineData("Digest realm=\"biloxi.com\", nonce=\"abc\", algorithm=SHA-512-256")] // .NET has no SHA-512/256
    public void Unusable_challenges_are_rejected(string? challenge)
    {
        var auth = new SipDigestAuthentication();

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, Method, Uri, nonceCount: 1, out var header);

        Assert.False(ok);
        Assert.Equal(string.Empty, header);
    }
}
