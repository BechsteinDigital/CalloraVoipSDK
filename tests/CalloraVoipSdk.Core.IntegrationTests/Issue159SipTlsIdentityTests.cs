using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #159 P1: <see cref="SipDomainCertificateValidator"/> must extract and compare SIP
/// domain identities strictly per RFC 5922 §7.1/§7.2 — only <c>sip:</c> URI SANs without
/// userinfo are domain identities, URI identities take precedence over <c>dNSName</c>, no
/// wildcard/suffix expansion, and IDNs are canonicalized to A-labels before comparison.
/// These are the RFC-correct negatives that replace the earlier permissive positives.
/// </summary>
public sealed class Issue159SipTlsIdentityTests
{
    [Fact]
    public void Sips_uri_san_is_not_a_sip_domain_identity()
    {
        // RFC 5922 §7.2: only the "sip" scheme identifies a SIP domain; "sips" does not.
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sips:secure.example.com"));
        using var cert = CreateSelfSigned("CN=sips-test", san);

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "secure.example.com"));
    }

    [Fact]
    public void Sip_uri_san_with_userinfo_is_rejected()
    {
        // RFC 5922 §7.2: a SIP URI SAN carrying userinfo identifies a user, not a domain, and
        // MUST be rejected in full — the host part must not be salvaged.
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sip:alice@user.example.com"));
        using var cert = CreateSelfSigned("CN=userinfo-test", san);

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "user.example.com"));
    }

    [Fact]
    public void Sips_uri_san_does_not_suppress_dns_name_fallback()
    {
        // A "sips:" URI is not a valid sip: domain identity, so it must NOT suppress the dNSName
        // fallback (RFC 5922 §7.2) — a co-present matching dNSName still verifies the domain.
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sips:secure.example.com"));
        san.AddDnsName("secure.example.com");
        using var cert = CreateSelfSigned("CN=sips-dns-fallback", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "secure.example.com"));
    }

    [Fact]
    public void Userinfo_sip_uri_san_does_not_suppress_dns_name_fallback()
    {
        // A "sip:user@host" URI is rejected as a domain identity, so it likewise must not suppress
        // the dNSName fallback — a co-present matching dNSName still verifies the domain.
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sip:alice@user.example.com"));
        san.AddDnsName("user.example.com");
        using var cert = CreateSelfSigned("CN=userinfo-dns-fallback", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "user.example.com"));
    }

    [Fact]
    public void Plain_sip_uri_san_host_matches_its_domain_only()
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sip:proxy.example.com"));
        using var cert = CreateSelfSigned("CN=sip-uri-test", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "proxy.example.com"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "other.example.com"));
    }

    [Fact]
    public void Valid_sip_uri_identity_suppresses_dns_name_fallback()
    {
        // RFC 5922 §7.2: when at least one valid SIP-URI domain identity is present, dNSName
        // entries MUST NOT be consulted as a fallback.
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("sip:primary.example.com"));
        san.AddDnsName("fallback.example.com");
        using var cert = CreateSelfSigned("CN=precedence-test", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "primary.example.com"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "fallback.example.com"));
    }

    [Fact]
    public void Wildcard_dns_san_is_not_expanded()
    {
        // RFC 5922 §7.2 forbids wildcard/suffix expansion; a wildcard label matches nothing here.
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("*.example.com");
        using var cert = CreateSelfSigned("CN=wildcard-test", san);

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "sub.example.com"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "example.com"));
    }

    [Fact]
    public void Idn_expected_domain_matches_a_label_dns_san()
    {
        // Real certificates store IDNs as ASCII A-labels in dNSName; the configured expected
        // domain may be the Unicode U-label. Both must canonicalize to the same A-label.
        var idn = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true };
        const string uLabel = "münchen.example";
        var aLabel = idn.GetAscii(uLabel); // e.g. "xn--mnchen-3ya.example"

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(aLabel);
        using var cert = CreateSelfSigned("CN=idn-test", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, uLabel));
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, aLabel));
    }

    [Fact]
    public void Exact_dns_san_still_matches_when_no_uri_identity_present()
    {
        // Regression guard: without any SIP-URI identity, a bare dNSName is compared exactly.
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("sip.example.com");
        using var cert = CreateSelfSigned("CN=dns-test", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "SIP.EXAMPLE.COM"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.other.com"));
    }

    private static X509Certificate2 CreateSelfSigned(string subject, SubjectAlternativeNameBuilder san)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
