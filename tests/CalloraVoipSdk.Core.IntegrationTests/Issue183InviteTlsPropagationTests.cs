using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (slice 3c-2): the per-line TLS override flows through the call/dialog chain
/// (SipInviteRequest → SipCallSessionConfiguration → ISipCallSessionContext → the per-dialog client
/// transaction) so an outbound INVITE presents the line's identity. A call without an override carries
/// none (behaviour-preserving).
/// </summary>
public sealed class Issue183InviteTlsPropagationTests
{
    [Fact]
    public async Task Outbound_invite_carries_the_per_line_tls_identity()
    {
        using var cert = SelfSigned();
        var lineTls = new TlsConfiguration { ClientCertificate = cert };
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "INVITE" ? Ok(req) : null
        };
        using var service = new SipCallSignalingService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        await service.InviteAsync(Invite(lineTls));

        var invite = transport.SnapshotRequests().Single(r => r.Method == "INVITE");
        Assert.Same(lineTls, invite.LineTls);
    }

    [Fact]
    public async Task Outbound_invite_without_an_override_carries_no_identity()
    {
        using var transport = new CapturingSipTransportRuntime
        {
            ResponseFactory = req => req.Method == "INVITE" ? Ok(req) : null
        };
        using var service = new SipCallSignalingService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        await service.InviteAsync(Invite(lineTls: null));

        Assert.Null(transport.SnapshotRequests().Single(r => r.Method == "INVITE").LineTls);
    }

    private static SipInviteRequest Invite(TlsConfiguration? lineTls) => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        RemoteUri = "sip:bob@192.0.2.10",
        SessionDescription = "v=0\r\n",
        Timeout = TimeSpan.FromSeconds(5),
        Transport = SipTransportProtocol.Tls,
        LineTls = lineTls
    };

    private static SipResponse Ok(CapturedSipRequest request)
    {
        var toHeader = request.Headers["To"];
        if (SipProtocol.ExtractTag(toHeader) is null)
            toHeader = $"{toHeader};tag=remote-tag";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = toHeader,
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
            ["Contact"] = "<sip:bob@192.0.2.10:5060>",
        };
        return new SipResponse(200, "OK", headers, string.Empty);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=issue183-call", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}
