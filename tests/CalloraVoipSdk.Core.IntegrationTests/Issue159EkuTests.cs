using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #159 P2: <see cref="SipDomainCertificateValidator"/> must apply the RFC 5924 §5 Extended Key
/// Usage policy for SIP domain certificates. A present EKU extension that carries neither
/// id-kp-sipDomain (1.3.6.1.5.5.7.3.20) nor anyExtendedKeyUsage does not authorize the certificate as a
/// SIP domain certificate; an absent EKU is accepted per local policy.
/// </summary>
public sealed class Issue159EkuTests
{
    private const string SipDomainEku = "1.3.6.1.5.5.7.3.20";
    private const string ServerAuthEku = "1.3.6.1.5.5.7.3.1";
    private const string AnyExtendedKeyUsage = "2.5.29.37.0";

    [Fact]
    public void Certificate_without_eku_is_accepted()
    {
        using var cert = CreateSelfSigned("sip.example.com");
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
    }

    [Fact]
    public void Eku_with_sip_domain_is_accepted()
    {
        using var cert = CreateSelfSigned("sip.example.com", SipDomainEku);
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
    }

    [Fact]
    public void Eku_with_any_extended_key_usage_is_accepted()
    {
        using var cert = CreateSelfSigned("sip.example.com", AnyExtendedKeyUsage);
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
    }

    [Fact]
    public void Eku_with_sip_domain_among_other_usages_is_accepted()
    {
        using var cert = CreateSelfSigned("sip.example.com", ServerAuthEku, SipDomainEku);
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
    }

    [Fact]
    public void Eku_without_sip_domain_or_any_is_rejected()
    {
        // EKU present but restricted to serverAuth only — not authorized as a SIP domain cert (RFC 5924 §5).
        using var cert = CreateSelfSigned("sip.example.com", ServerAuthEku);
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));
    }

    private static X509Certificate2 CreateSelfSigned(string dnsName, params string[] ekuOids)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());

        if (ekuOids.Length > 0)
        {
            var oids = new OidCollection();
            foreach (var oid in ekuOids)
                oids.Add(new Oid(oid));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
