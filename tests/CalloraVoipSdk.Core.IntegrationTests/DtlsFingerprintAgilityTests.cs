using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #116 — the fingerprint hash is the <em>peer's</em> choice (RFC 8122 §5). We only ever offer SHA-256, but a
/// peer may present its own certificate under another function, and the verification has to digest that
/// certificate with the function it named. Previously anything but <c>sha-256</c> was rejected outright with
/// <c>unsupported_certificate</c>, so such a peer could not complete a DTLS-SRTP handshake at all.
///
/// <para>
/// Digests are checked against the FIPS 180 test vectors for "abc", not against our own output, so the tests
/// fail if the algorithm mapping is wrong rather than agreeing with a mistake.
/// </para>
/// </summary>
public sealed class DtlsFingerprintAgilityTests
{
    private static ReadOnlySpan<byte> Abc => "abc"u8;

    private static string Digest(string algorithm) =>
        DtlsFingerprint.FromDerCertificate(Abc, algorithm).Value.Replace(":", "", StringComparison.Ordinal);

    // ── The digests are the real ones ────────────────────────────────────────

    [Fact]
    public void Sha256_matches_the_published_vector() =>
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            Digest("sha-256"));

    [Fact]
    public void Sha384_matches_the_published_vector() =>
        Assert.Equal(
            "CB00753F45A35E8BB5A03D699AC65007272C32AB0EDED1631A8B605A43FF5BED8086072BA1E7CC2358BAECA134C825A7",
            Digest("sha-384"));

    [Fact]
    public void Sha512_matches_the_published_vector() =>
        Assert.Equal(
            "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A"
            + "2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F",
            Digest("sha-512"));

    [Fact]
    public void Sha224_matches_the_published_vector() =>
        // .NET has no SHA-224; this one goes through BouncyCastle, so the vector is what proves the
        // detour produces the same bytes.
        Assert.Equal("23097D223405D8228642A477BDA255B32AADBCE4BDA0B3F7E36C9DA7", Digest("sha-224"));

    [Fact]
    public void Each_algorithm_yields_its_own_digest_length()
    {
        // A wrong mapping that still produced *some* digest would slip past a single-algorithm test.
        Assert.Equal(28 * 3 - 1, DtlsFingerprint.FromDerCertificate(Abc, "sha-224").Value.Length);
        Assert.Equal(32 * 3 - 1, DtlsFingerprint.FromDerCertificate(Abc, "sha-256").Value.Length);
        Assert.Equal(48 * 3 - 1, DtlsFingerprint.FromDerCertificate(Abc, "sha-384").Value.Length);
        Assert.Equal(64 * 3 - 1, DtlsFingerprint.FromDerCertificate(Abc, "sha-512").Value.Length);
    }

    // ── Which functions are accepted, and which are refused on purpose ───────

    [Theory]
    [InlineData("sha-224")]
    [InlineData("sha-256")]
    [InlineData("sha-384")]
    [InlineData("sha-512")]
    [InlineData("SHA-256")]   // RFC 8122 tokens are compared case-insensitively
    [InlineData(" sha-256 ")] // tolerate surrounding whitespace from the SDP line
    public void Strong_functions_are_accepted(string algorithm) =>
        Assert.True(DtlsFingerprint.IsSupportedAlgorithm(algorithm));

    [Theory]
    [InlineData("md2")]
    [InlineData("md5")]
    [InlineData("sha-1")]
    public void Collision_prone_functions_are_refused(string algorithm)
    {
        // A fingerprint breaks on a collision, not a preimage: an attacker mints two certificates with the
        // same digest, signals one and presents the other — the 2008 rogue-CA attack on MD5. Since this
        // fingerprint is the only binding between signalled identity and DTLS endpoint (RFC 8122 §6), the
        // registry entry alone is not reason enough to compute it.
        Assert.False(DtlsFingerprint.IsSupportedAlgorithm(algorithm));
        Assert.Throws<ArgumentException>(() => DtlsFingerprint.FromDerCertificate(Abc, algorithm));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256")]     // missing hyphen — not the registered token
    [InlineData("sha-257")]
    [InlineData("nonsense")]
    public void Unknown_tokens_are_refused(string algorithm) =>
        Assert.False(DtlsFingerprint.IsSupportedAlgorithm(algorithm));

    [Fact]
    public void A_null_algorithm_is_refused_rather_than_throwing() =>
        Assert.False(DtlsFingerprint.IsSupportedAlgorithm(null));

    // ── Comparison across algorithms ─────────────────────────────────────────

    [Fact]
    public void A_fingerprint_matches_itself_under_a_non_default_algorithm()
    {
        // The case the old code could not handle: peer signals sha-384, we digest its certificate with
        // sha-384, and the two agree.
        var signalled = DtlsFingerprint.FromDerCertificate(Abc, "sha-384");
        var computed = DtlsFingerprint.FromDerCertificate(Abc, "sha-384");

        Assert.True(computed.Matches(signalled));
    }

    [Fact]
    public void Digests_of_different_algorithms_never_match()
    {
        var sha256 = DtlsFingerprint.FromDerCertificate(Abc, "sha-256");
        var sha384 = DtlsFingerprint.FromDerCertificate(Abc, "sha-384");

        Assert.False(sha256.Matches(sha384));
    }

    [Fact]
    public void A_different_certificate_does_not_match_under_any_algorithm()
    {
        var other = Encoding.ASCII.GetBytes("abd");

        foreach (var algorithm in new[] { "sha-224", "sha-256", "sha-384", "sha-512" })
        {
            var expected = DtlsFingerprint.FromDerCertificate(Abc, algorithm);
            var actual = DtlsFingerprint.FromDerCertificate(other, algorithm);
            Assert.False(actual.Matches(expected), $"digest collision claimed for {algorithm}");
        }
    }

    [Fact]
    public void The_default_overload_still_emits_sha256()
    {
        // What we put in our own SDP must not move: SHA-256 is what RFC 8122 §5 requires everyone to
        // support, so it is the interoperable choice for the offer.
        Assert.Equal(DtlsFingerprint.Sha256Algorithm, DtlsFingerprint.FromDerCertificate(Abc).Algorithm);
    }
}
