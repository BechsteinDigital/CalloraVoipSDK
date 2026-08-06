using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [DTLS] #192: the certificate fingerprint is the only credential binding the DTLS-SRTP connection to the
/// signaled identity, so its digest must be compared in constant time (ENGINEERING_RULES K5) — never with a
/// short-circuiting string compare. These tests pin equality (case-insensitive, per RFC 8122 §5), single-octet
/// rejection, and fail-closed handling of a malformed or differently-shaped digest.
/// </summary>
public sealed class DtlsFingerprintMatchTests
{
    private const string Digest =
        "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    private static DtlsFingerprint Fp(string algorithm, string value) =>
        new() { Algorithm = algorithm, Value = value };

    [Fact]
    public void Matches_is_true_for_the_same_digest_regardless_of_hex_case()
    {
        var upper = Fp(DtlsFingerprint.Sha256Algorithm, Digest);
        var lower = Fp("SHA-256", Digest.ToLowerInvariant());

        Assert.True(upper.Matches(lower));
        Assert.True(lower.Matches(upper));
    }

    [Fact]
    public void Matches_is_false_when_a_single_octet_differs()
    {
        var a = Fp(DtlsFingerprint.Sha256Algorithm, Digest);
        // Flip only the last octet (…89 → …88) — a constant-time compare must still reject it.
        var b = Fp(DtlsFingerprint.Sha256Algorithm, Digest[..^1] + "8");

        Assert.False(a.Matches(b));
    }

    [Fact]
    public void Matches_is_false_for_a_different_algorithm_token()
    {
        var sha256 = Fp(DtlsFingerprint.Sha256Algorithm, Digest);
        var sha1 = Fp("sha-1", Digest);

        Assert.False(sha256.Matches(sha1));
    }

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("AB:CD:E")]                // ragged final group / wrong total length
    [InlineData("AB-CD-EF")]               // wrong separator
    [InlineData("GG:HH:II")]               // non-hex characters
    [InlineData("ABCDEF0123")]             // no separators
    public void Matches_fails_closed_for_a_malformed_digest_without_throwing(string malformed)
    {
        var good = Fp(DtlsFingerprint.Sha256Algorithm, Digest);
        var bad = Fp(DtlsFingerprint.Sha256Algorithm, malformed);

        Assert.False(good.Matches(bad));
        Assert.False(bad.Matches(good));
    }

    [Fact]
    public void Matches_is_false_for_a_differently_sized_digest()
    {
        var full = Fp(DtlsFingerprint.Sha256Algorithm, Digest);
        var truncated = Fp(DtlsFingerprint.Sha256Algorithm, "AB:CD:EF:01"); // 4 bytes vs 32

        Assert.False(full.Matches(truncated));
    }

    [Fact]
    public void FromDerCertificate_digest_matches_itself()
    {
        var der = Encoding.ASCII.GetBytes("not-a-real-certificate-but-hashable");
        var a = DtlsFingerprint.FromDerCertificate(der);
        var b = DtlsFingerprint.FromDerCertificate(der);

        Assert.True(a.Matches(b));
    }
}
