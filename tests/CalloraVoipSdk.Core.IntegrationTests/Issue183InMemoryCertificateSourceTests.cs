using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (slice 2): a caller may supply the SIP TLS identity certificate in memory via
/// <see cref="TlsConfiguration.ClientCertificate"/> instead of a file path. The in-memory source
/// takes precedence over <see cref="TlsConfiguration.CertificatePath"/> and is returned as the exact
/// caller instance (the SDK neither copies nor disposes it).
/// </summary>
public sealed class Issue183InMemoryCertificateSourceTests
{
    [Fact]
    public void In_memory_certificate_is_returned_when_configured()
    {
        using var cert = SelfSigned();
        var provider = new SipTlsCertificateProvider(new TlsConfiguration { ClientCertificate = cert });

        Assert.Same(cert, provider.GetCertificate());
    }

    [Fact]
    public void In_memory_certificate_takes_precedence_over_a_file_path()
    {
        using var cert = SelfSigned();
        // A non-existent path proves precedence: the in-memory source wins without ever touching disk.
        var provider = new SipTlsCertificateProvider(new TlsConfiguration
        {
            ClientCertificate = cert,
            CertificatePath = "/does/not/exist.pfx"
        });

        Assert.Same(cert, provider.GetCertificate());
    }

    [Fact]
    public void No_source_configured_returns_null()
    {
        var provider = new SipTlsCertificateProvider(new TlsConfiguration());

        Assert.Null(provider.GetCertificate());
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=in-memory-cert-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
