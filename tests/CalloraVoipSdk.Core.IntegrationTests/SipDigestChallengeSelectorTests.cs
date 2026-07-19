using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 8760 §2.4 challenge selection: when a registrar offers several Digest challenges the client
/// must pick the strongest algorithm it can actually compute. These tests exercise
/// <see cref="SipDigestChallengeSelector"/> directly with multiple WWW-Authenticate rows (stored
/// newline-separated), covering the multi-challenge path the end-to-end retry tests cannot reach
/// through a single challenge.
/// </summary>
public sealed class SipDigestChallengeSelectorTests
{
    private const string Realm = "biloxi.com";
    private const string Nonce = "abc123";

    private static SipResponse WithWwwAuthenticate(string wwwAuthenticateRows) =>
        new(401, "Unauthorized",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WWW-Authenticate"] = wwwAuthenticateRows,
            },
            string.Empty);

    [Fact]
    public void Among_multiple_challenges_the_strongest_computable_algorithm_wins()
    {
        // Two separate WWW-Authenticate rows: MD5 and SHA-512-256. SHA-512-256 is now computable,
        // so it must be selected even though MD5 is also on offer.
        var md5 = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=MD5";
        var sha512 = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-512-256";
        var response = WithWwwAuthenticate($"{md5}\n{sha512}");

        var ok = SipDigestChallengeSelector.TrySelect(response, out var challenge, out var authHeaderName);

        Assert.True(ok);
        Assert.Contains("SHA-512-256", challenge);
        Assert.Equal("Authorization", authHeaderName);
    }

    [Fact]
    public void An_uncomputable_but_stronger_looking_algorithm_never_beats_a_supported_one()
    {
        // The registrar offers an unsupported algorithm (SHA-512, no managed/agreed primitive) plus
        // MD5. The selector must fall back to the strongest *computable* challenge (MD5), not stall on
        // the unsupported one — this is the fallback the RFC 8760 deadlock fix depends on.
        var unsupported = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-512";
        var md5 = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=MD5";
        var response = WithWwwAuthenticate($"{unsupported}\n{md5}");

        var ok = SipDigestChallengeSelector.TrySelect(response, out var challenge, out _);

        Assert.True(ok);
        Assert.Contains("algorithm=MD5", challenge);
        Assert.DoesNotContain("SHA-512", challenge);
    }

    [Fact]
    public void A_sole_unsupported_challenge_is_not_selected()
    {
        // Only an uncomputable algorithm is offered: the selector reports no usable challenge rather
        // than returning one that would fail at the hashing step (Score -1, not 0).
        var response = WithWwwAuthenticate($"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-512");

        var ok = SipDigestChallengeSelector.TrySelect(response, out _, out _);

        Assert.False(ok);
    }
}
