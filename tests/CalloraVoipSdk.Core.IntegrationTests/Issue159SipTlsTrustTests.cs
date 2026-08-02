using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #159 P1: the SIP TLS server-certificate trust decision must be fail-closed. A missing
/// certificate is rejected in every trust mode, <see cref="TlsConfiguration.ExpectedSipDomain"/> is
/// always enforced (even under <see cref="SipTlsTrustMode.DangerousAcceptAnyChain"/>), and the
/// dangerous mode relaxes only standard chain/hostname trust — never the SIP-domain identity check.
/// </summary>
public sealed class Issue159SipTlsTrustTests
{
    private static readonly Func<X509Certificate2, bool> DomainMatches = _ => true;
    private static readonly Func<X509Certificate2, bool> DomainRejects = _ => false;

    [Theory]
    [InlineData(SipTlsTrustMode.System)]
    [InlineData(SipTlsTrustMode.DangerousAcceptAnyChain)]
    public void Missing_certificate_is_rejected_in_every_mode(SipTlsTrustMode mode)
    {
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            mode, expectedSipDomain: null, certificate: null, SslPolicyErrors.None, sipDomainMatches: null);

        Assert.False(accepted);
    }

    [Fact]
    public void System_mode_rejects_standard_policy_errors()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, null, cert, SslPolicyErrors.RemoteCertificateChainErrors, null);

        Assert.False(accepted);
    }

    [Fact]
    public void Dangerous_mode_accepts_policy_errors_when_no_sip_domain_configured()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.DangerousAcceptAnyChain, null, cert,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, null);

        Assert.True(accepted);
    }

    [Fact]
    public void Dangerous_mode_still_enforces_expected_sip_domain_on_mismatch()
    {
        // The core P1: DangerousAcceptAnyChain must NOT silently disable the identity check.
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.DangerousAcceptAnyChain, "example.com", cert,
            SslPolicyErrors.RemoteCertificateChainErrors, DomainRejects);

        Assert.False(accepted);
    }

    [Fact]
    public void Dangerous_mode_accepts_when_expected_sip_domain_matches()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.DangerousAcceptAnyChain, "example.com", cert,
            SslPolicyErrors.RemoteCertificateChainErrors, DomainMatches);

        Assert.True(accepted);
    }

    [Fact]
    public void System_mode_accepts_clean_certificate_with_matching_domain()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert, SslPolicyErrors.None, DomainMatches);

        Assert.True(accepted);
    }

    [Fact]
    public void System_mode_rejects_clean_certificate_with_mismatching_domain()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert, SslPolicyErrors.None, DomainRejects);

        Assert.False(accepted);
    }

    [Fact]
    public void Expected_sip_domain_without_a_provider_fails_closed()
    {
        // ExpectedSipDomain configured but no certificate provider available → reject, never accept.
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert, SslPolicyErrors.None, sipDomainMatches: null);

        Assert.False(accepted);
    }

    [Fact]
    public void System_mode_accepts_clean_certificate_without_domain_check()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, null, cert, SslPolicyErrors.None, null);

        Assert.True(accepted);
    }

    [Fact]
    public void Name_mismatch_is_rescued_by_a_matching_sip_domain_identity()
    {
        // RFC 5922 §7.3: a URI-only (or non-matching-dNSName) SIP identity trips the standard
        // hostname check; a successful strict RFC 5922 match must rescue that pure name mismatch.
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert,
            SslPolicyErrors.RemoteCertificateNameMismatch, DomainMatches);

        Assert.True(accepted);
    }

    [Fact]
    public void Name_mismatch_without_a_matching_sip_domain_is_rejected()
    {
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert,
            SslPolicyErrors.RemoteCertificateNameMismatch, DomainRejects);

        Assert.False(accepted);
    }

    [Fact]
    public void Name_mismatch_without_a_configured_sip_domain_stays_terminal()
    {
        // Nothing to rescue the mismatch against — it must stay terminal.
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, null, cert,
            SslPolicyErrors.RemoteCertificateNameMismatch, null);

        Assert.False(accepted);
    }

    [Fact]
    public void Chain_error_stays_terminal_even_when_sip_domain_matches()
    {
        // Only a pure hostname mismatch may be rescued; chain/time/usage/revocation errors are
        // terminal regardless of the RFC 5922 identity result.
        using var cert = SelfSigned();
        var (accepted, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", cert,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, DomainMatches);

        Assert.False(accepted);
    }

    [Fact]
    public void Base_x509_certificate_is_upgraded_and_domain_checked()
    {
        // Exercises the exact branch that closed the old `certificate is X509Certificate2` fail-open:
        // a base X509Certificate must be converted to X509Certificate2 and still domain-checked.
        using var cert2 = SelfSigned();
#pragma warning disable SYSLIB0057 // A base-typed X509Certificate instance is required for this path.
        using var baseCert = new X509Certificate(cert2.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057

        var (matched, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", baseCert, SslPolicyErrors.None, DomainMatches);
        Assert.True(matched);

        var (mismatched, _) = SipTlsServerTrustEvaluator.Evaluate(
            SipTlsTrustMode.System, "example.com", baseCert, SslPolicyErrors.None, DomainRejects);
        Assert.False(mismatched);
    }

    [Theory]
    [InlineData(true, SipTlsTrustMode.DangerousAcceptAnyChain)]
    [InlineData(false, SipTlsTrustMode.System)]
    public void Obsolete_accept_untrusted_alias_round_trips_to_trust_mode(bool accept, SipTlsTrustMode expected)
    {
        // Back-compat contract: the deprecated bool must map to and reflect TrustMode (non-breaking).
#pragma warning disable CS0618 // AcceptUntrustedCertificates is intentionally exercised here.
        var config = new TlsConfiguration { AcceptUntrustedCertificates = accept };
        Assert.Equal(expected, config.TrustMode);
        Assert.Equal(accept, config.AcceptUntrustedCertificates);
#pragma warning restore CS0618
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=trust-eval-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
