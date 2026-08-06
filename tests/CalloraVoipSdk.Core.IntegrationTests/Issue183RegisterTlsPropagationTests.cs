using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (slice 3c-1): the per-line TLS override bound at the connect boundary reaches the
/// transport on the REGISTER send — flowing SipRegistrationRequest.LineTls through the client
/// transaction to the identity-aware SendRequestAsync overload. A line without an override carries
/// no identity (behaviour-preserving).
/// </summary>
public sealed class Issue183RegisterTlsPropagationTests
{
    [Fact]
    public async Task Register_carries_the_per_line_tls_identity_to_the_transport()
    {
        using var cert = SelfSigned();
        var lineTls = new TlsConfiguration { ClientCertificate = cert };
        var transport = new CapturingSipTransportRuntime { ResponseFactory = Echo200 };
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        await service.RegisterAsync(new SipRegistrationRequest
        {
            Username = "user",
            Password = string.Empty,
            Domain = "pbx.example.com",
            Port = 5061,
            Transport = SipTransportProtocol.Tls,
            Timeout = TimeSpan.FromSeconds(2),
            LineTls = lineTls
        });

        var register = transport.SnapshotRequests().Single(r => r.Method == "REGISTER");
        Assert.Same(lineTls, register.LineTls);
    }

    [Fact]
    public async Task Register_without_a_line_override_carries_no_identity()
    {
        var transport = new CapturingSipTransportRuntime { ResponseFactory = Echo200 };
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        await service.RegisterAsync(new SipRegistrationRequest
        {
            Username = "user",
            Password = string.Empty,
            Domain = "pbx.example.com",
            Port = 5060,
            Timeout = TimeSpan.FromSeconds(2)
        });

        Assert.Null(transport.SnapshotRequests().Single(r => r.Method == "REGISTER").LineTls);
    }

    private static SipResponse Echo200(CapturedSipRequest req)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
            ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:user@127.0.0.1:5060>",
        };
        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=issue183-line", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
