using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (review H1): out-of-dialog MESSAGE and PUBLISH are line-originated too, so they must
/// carry the per-line TLS identity to the transport — otherwise a line with an mTLS override would
/// present the client-wide default certificate for those requests.
/// </summary>
public sealed class Issue183MessagePublishTlsPropagationTests
{
    [Fact]
    public async Task Outbound_message_carries_the_per_line_tls_identity()
    {
        using var cert = SelfSigned();
        var lineTls = new TlsConfiguration { ClientCertificate = cert };
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "MESSAGE" ? Ok(req) : null
        };
        var messages = new SipCallSignalingMessages(
            transport,
            new NoopSipDigestAuthenticator(),
            new SipClientTransactionExecutor(transport, NullLogger.Instance),
            NullLogger.Instance);

        await messages.SendMessageAsync(new SipMessageRequest
        {
            LocalUsername = "alice",
            LocalDomain = "example.com",
            RemoteUri = "sip:bob@192.0.2.10",
            Body = "hi",
            Transport = SipTransportProtocol.Tls,
            LineTls = lineTls
        });

        Assert.Same(lineTls, transport.SnapshotRequests().Single(r => r.Method == "MESSAGE").LineTls);
    }

    [Fact]
    public async Task Outbound_publish_carries_the_per_line_tls_identity()
    {
        using var cert = SelfSigned();
        var lineTls = new TlsConfiguration { ClientCertificate = cert };
        var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "PUBLISH" ? Ok(req) : null
        };
        var publications = new SipCallSignalingPublications(
            transport,
            new NoopSipDigestAuthenticator(),
            new SipClientTransactionExecutor(transport, NullLogger.Instance),
            NullLogger.Instance);

        await publications.PublishAsync(new SipPublishRequest
        {
            LocalUsername = "alice",
            LocalDomain = "example.com",
            RemoteUri = "sip:alice@example.com",
            EventType = "presence",
            Body = "<presence/>",
            ContentType = "application/pidf+xml",
            Transport = SipTransportProtocol.Tls,
            LineTls = lineTls
        });

        Assert.Same(lineTls, transport.SnapshotRequests().Single(r => r.Method == "PUBLISH").LineTls);
    }

    private static SipResponse Ok(CapturedSipRequest req)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
        };
        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=issue183-im", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
