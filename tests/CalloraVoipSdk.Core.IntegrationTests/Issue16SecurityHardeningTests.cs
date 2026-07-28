using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Media;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Security-cluster fixes for Issue #16: AES-GCM recording encryption (in-place + key wipe),
/// thread-safe TLS certificate loading, and ASN.1-based SAN validation.
/// </summary>
public sealed class Issue16SecurityHardeningTests
{
    // ── Media 2: AES-GCM recording encryption ────────────────────────────────────────────────

    [Fact]
    public async Task AesGcm_encrypt_roundtrips_through_streaming_decrypt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        // Larger than one 64 KiB chunk so the roundtrip exercises the multi-chunk STREAM path.
        var plaintext = RandomNumberGenerator.GetBytes(200_000);
        var inputPath = Path.GetTempFileName();
        var encryptedPath = inputPath + ".enc";
        var decryptedPath = inputPath + ".dec";
        try
        {
            await File.WriteAllBytesAsync(inputPath, plaintext);
            using (var provider = new AesGcmRecordingEncryptionProvider(key))
            {
                await provider.EncryptFileAsync(inputPath, encryptedPath);
                await provider.DecryptFileAsync(encryptedPath, decryptedPath);
            }

            var enc = await File.ReadAllBytesAsync(encryptedPath);
            // Streaming format header: "VREC2"(5) + salt(16) + noncePrefix(7); ciphertext is chunked.
            Assert.Equal("VREC2", Encoding.ASCII.GetString(enc, 0, 5));
            // Header + per-chunk tags make the container strictly larger than the raw plaintext.
            Assert.True(enc.Length > plaintext.Length);

            var decrypted = await File.ReadAllBytesAsync(decryptedPath);
            Assert.Equal(plaintext, decrypted);
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(encryptedPath);
            File.Delete(decryptedPath);
        }
    }

    [Fact]
    public async Task AesGcm_encrypt_after_dispose_throws()
    {
        var provider = new AesGcmRecordingEncryptionProvider(RandomNumberGenerator.GetBytes(32));
        provider.Dispose(); // wipes the key

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await provider.EncryptFileAsync("in.wav", "out.enc"));
    }

    // ── Security 4: thread-safe TLS certificate load ─────────────────────────────────────────

    [Fact]
    public void TlsConfiguration_GetCertificate_loads_once_under_concurrency()
    {
        var pfxPath = Path.GetTempFileName();
        const string password = "issue16-test-pw";
        try
        {
            using (var cert = CreateSelfSigned("CN=tls-test"))
                File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pkcs12, password));

            var config = new TlsConfiguration { CertificatePath = pfxPath, CertificatePassword = password };

            // Fire all callers at once to expose a check-then-load race.
            var results = new X509Certificate2?[64];
            using var gate = new Barrier(results.Length);
            Parallel.For(0, results.Length, i =>
            {
                gate.SignalAndWait();
                results[i] = config.GetCertificate();
            });

            var first = results[0];
            Assert.NotNull(first);
            // A single cached instance for every caller — a check-then-load race would hand out
            // (and leak) more than one X509Certificate2.
            Assert.All(results, c => Assert.Same(first, c));
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    // ── Security 3: ASN.1-based SAN validation ───────────────────────────────────────────────

    [Fact]
    public void ValidateSipDomain_matches_dns_and_sip_uri_sans_via_asn1()
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("sip.example.com");
        san.AddDnsName("*.wild.example.com");
        san.AddUri(new Uri("sip:proxy@uri.example.com"));
        using var cert = CreateSelfSigned("CN=san-test", san);

        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sip.example.com"));      // dNSName exact
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "sub.wild.example.com")); // dNSName wildcard
        Assert.True(SipDomainCertificateValidator.ValidateSipDomain(cert, "uri.example.com"));      // sip: URI host
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "other.example.com"));
        Assert.False(SipDomainCertificateValidator.ValidateSipDomain(cert, "wild.example.com"));    // *.wild != wild base

        var names = SipDomainCertificateValidator.GetSubjectAlternativeNames(cert);
        Assert.Contains("sip.example.com", names);
        Assert.Contains(names, n => n.Contains("uri.example.com", StringComparison.Ordinal));
    }

    private static X509Certificate2 CreateSelfSigned(string subject, SubjectAlternativeNameBuilder? san = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (san is not null)
            request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
