using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #159 P2: <see cref="SipDomainCertificateValidator"/> must bound the peer-controlled work of
/// SAN decoding — hard caps on entry count, per-value size and aggregate size — and reject a SAN
/// extension with trailing data after the GeneralNames SEQUENCE. Over-limit and malformed input fail
/// closed.
/// </summary>
public sealed class Issue159SanCapsTests
{
    private const string SanOid = "2.5.29.17";

    [Fact]
    public void Too_many_san_entries_fails_closed_even_with_a_match()
    {
        var san = new SubjectAlternativeNameBuilder();
        for (var i = 0; i < 150; i++)
            san.AddDnsName($"host{i}.example.com");
        san.AddDnsName("match.example.com");
        using var cert = CreateSelfSigned("CN=many-sans", san);

        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "match.example.com"));
    }

    [Fact]
    public void A_small_san_set_still_validates()
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("match.example.com");
        using var cert = CreateSelfSigned("CN=few-sans", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "match.example.com"));
    }

    [Fact]
    public void Trailing_data_after_the_san_sequence_fails_closed()
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddDnsName("trailing.example.com");
        var clean = builder.Build().RawData;
        var tampered = new byte[clean.Length + 1];
        Array.Copy(clean, tampered, clean.Length); // one trailing 0x00 after the SEQUENCE

        using var certClean = CreateSelfSignedWithRawSan("CN=clean", clean);
        using var certTampered = CreateSelfSignedWithRawSan("CN=tampered", tampered);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(certClean, "trailing.example.com"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(certTampered, "trailing.example.com"));
    }

    [Fact]
    public void Oversized_san_value_is_skipped_without_appearing_in_diagnostics()
    {
        // Craft the SAN DER directly: SubjectAlternativeNameBuilder rejects non-IDN dNSNames, but an
        // IA5String carries arbitrary ASCII, which is exactly the oversized value the cap must skip.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
            writer.WriteCharacterString(UniversalTagNumber.IA5String, "ok.example.com", dnsTag);
            writer.WriteCharacterString(UniversalTagNumber.IA5String, new string('a', 1500), dnsTag);
        }
        using var cert = CreateSelfSignedWithRawSan("CN=oversized-san", writer.Encode());

        var names = SipDomainCertificateValidator.GetSubjectAlternativeNames(cert);

        Assert.Contains("ok.example.com", names);
        Assert.DoesNotContain(names, n => n.Length > 1024);
    }

    private static X509Certificate2 CreateSelfSigned(string subject, SubjectAlternativeNameBuilder san)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static X509Certificate2 CreateSelfSignedWithRawSan(string subject, byte[] rawSan)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509Extension(new Oid(SanOid), rawSan, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
