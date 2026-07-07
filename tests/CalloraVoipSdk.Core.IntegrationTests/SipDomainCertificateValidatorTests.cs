using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Behavioural tests for <see cref="SipDomainCertificateValidator"/> using real
/// self-signed certificates produced via <see cref="CertificateRequest"/> and
/// <see cref="SubjectAlternativeNameBuilder"/>. These exercise the ASN.1-based
/// SAN decoding path rather than the previous, brittle
/// <see cref="X509Extension.Format(bool)"/> string scraping.
/// </summary>
public sealed class SipDomainCertificateValidatorTests
{
    [Fact]
    public void ValidateSipDomain_DnsNameExactMatch_ReturnsTrue()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("example.com"));

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_DnsNameIsCaseInsensitive_ReturnsTrue()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("Sip.Example.COM"));

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "sip.example.com"));
    }

    [Fact]
    public void ValidateSipDomain_DnsNameMismatch_ReturnsFalse()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("other.example.org"));

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_WildcardDnsName_MatchesLeftmostLabel_ReturnsTrue()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("*.example.com"));

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "sub.example.com"));
    }

    [Fact]
    public void ValidateSipDomain_WildcardDnsName_DoesNotMatchBareDomain_ReturnsFalse()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("*.example.com"));

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_WildcardDnsName_DoesNotMatchDeeperSubdomain_ReturnsFalse()
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("*.example.com"));

        // Wildcard covers exactly one label: deep.sub.example.com must NOT match.
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "deep.sub.example.com"));
    }

    [Fact]
    public void ValidateSipDomain_SipUriSan_MatchesHostComponent_ReturnsTrue()
    {
        using var certificate = CreateCertificate(san => san.AddUri(new Uri("sip:proxy@example.com")));

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_SipsUriSanWithPortAndParams_MatchesHostComponent_ReturnsTrue()
    {
        using var certificate = CreateCertificate(
            san => san.AddUri(new Uri("sips:proxy@example.com:5061;transport=tcp")));

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_NonSipUriScheme_DoesNotMatch_ReturnsFalse()
    {
        using var certificate = CreateCertificate(san => san.AddUri(new Uri("https://example.com/")));

        // Only sip:/sips: URIs establish a SIP domain identity per RFC 5922 §7.1.
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_CommaContainingUriAndIpAddressSan_StillMatchesSip_NoFalseMatch()
    {
        // A URI value containing commas would break the old Format()+comma-split parser,
        // and an iPAddress SAN is a GeneralName choice that must be skipped, not matched.
        using var certificate = CreateCertificate(san =>
        {
            san.AddUri(new Uri("https://example.com/a,b,c"));
            san.AddIpAddress(IPAddress.Parse("192.0.2.10"));
            san.AddUri(new Uri("sip:proxy@example.com"));
        });

        // The genuine sip: identity still matches, despite the comma URI and IP entry.
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));

        // A comma-fragment that the old parser could have produced must NOT match.
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "b"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "192.0.2.10"));
    }

    [Fact]
    public void ValidateSipDomain_Rfc822NameSanOnly_IsSkipped_ReturnsFalse()
    {
        // rfc822Name ([1]) shares its host with the domain but is NOT a valid SIP
        // domain identity and must be skipped rather than matched.
        using var certificate = CreateCertificate(san => san.AddEmailAddress("admin@example.com"));

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_NoSanExtension_ReturnsFalse()
    {
        using var certificate = CreateCertificateWithoutSan();

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Fact]
    public void ValidateSipDomain_MalformedSanExtension_ReturnsFalse()
    {
        using var certificate = CreateCertificateWithRawSan(new byte[] { 0x30, 0x82, 0xFF, 0xFF });

        // Invalid DER must be handled via AsnContentException, not throw or assert a match.
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, "example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSipDomain_NullOrWhitespaceDomain_ReturnsFalse(string? sipDomain)
    {
        using var certificate = CreateCertificate(san => san.AddDnsName("example.com"));

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certificate, sipDomain!));
    }

    [Fact]
    public void GetSubjectAlternativeNames_ReturnsDecodedDnsAndUriValuesOnly()
    {
        using var certificate = CreateCertificate(san =>
        {
            san.AddDnsName("example.com");
            san.AddUri(new Uri("sip:proxy@example.com"));
            san.AddIpAddress(IPAddress.Parse("192.0.2.10"));
            san.AddEmailAddress("admin@example.com");
        });

        var values = SipDomainCertificateValidator.GetSubjectAlternativeNames(certificate);

        Assert.Contains("example.com", values);
        Assert.Contains("sip:proxy@example.com", values);
        // iPAddress and rfc822Name are not SIP-relevant choices and must be excluded.
        Assert.DoesNotContain("192.0.2.10", values);
        Assert.DoesNotContain("admin@example.com", values);
    }

    [Fact]
    public void GetSubjectAlternativeNames_PreservesCommaContainingUriValue()
    {
        using var certificate = CreateCertificate(san => san.AddUri(new Uri("https://example.com/a,b,c")));

        var values = SipDomainCertificateValidator.GetSubjectAlternativeNames(certificate);

        // The old Format()+comma-split parser would have fragmented this value.
        Assert.Contains("https://example.com/a,b,c", values);
    }

    [Fact]
    public void GetSubjectAlternativeNames_NoSanExtension_ReturnsEmpty()
    {
        using var certificate = CreateCertificateWithoutSan();

        Assert.Empty(SipDomainCertificateValidator.GetSubjectAlternativeNames(certificate));
    }

    [Fact]
    public void GetSubjectAlternativeNames_MalformedSanExtension_ReturnsEmpty()
    {
        using var certificate = CreateCertificateWithRawSan(new byte[] { 0x30, 0x82, 0xFF, 0xFF });

        Assert.Empty(SipDomainCertificateValidator.GetSubjectAlternativeNames(certificate));
    }

    // ──────────────────────────────────────────────────────────────
    // Certificate factories
    // ──────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateCertificate(Action<SubjectAlternativeNameBuilder> configureSan)
    {
        var sanBuilder = new SubjectAlternativeNameBuilder();
        configureSan(sanBuilder);

        var request = CreateRequest();
        request.CertificateExtensions.Add(sanBuilder.Build());
        return SelfSign(request);
    }

    private static X509Certificate2 CreateCertificateWithoutSan() => SelfSign(CreateRequest());

    private static X509Certificate2 CreateCertificateWithRawSan(byte[] rawSanBytes)
    {
        var request = CreateRequest();
        request.CertificateExtensions.Add(
            new X509Extension(new Oid("2.5.29.17"), rawSanBytes, critical: false));
        return SelfSign(request);
    }

    private static CertificateRequest CreateRequest()
    {
        // ECDsa keeps key generation fast; the signing algorithm is irrelevant to SAN parsing.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=callora-sip-validator-test", key, HashAlgorithmName.SHA256);
    }

    private static X509Certificate2 SelfSign(CertificateRequest request) =>
        request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
}
