using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-3 (K4): the SIP transport admits inbound stream/WebSocket connections against a per-IP cap
/// and bounds the TLS handshake, so a peer cannot pin an unbounded number of connections nor hold an
/// admission slot forever by stalling the handshake (slowloris). Both are enforced on the real runtime over
/// loopback sockets.
/// </summary>
public sealed class SipTransportConnectionAdmissionTests
{
    private static async Task<int> ReadTolerantAsync(NetworkStream stream)
    {
        try
        {
            return await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException)
        {
            return 0; // a connection reset is also the server dropping us
        }
    }

    [Fact]
    public async Task A_second_connection_from_the_same_ip_is_dropped_when_the_per_remote_cap_is_one()
    {
        using var runtime = new SipTransportRuntime(
            NullLoggerFactory.Instance,
            new SipWireProtocol(),
            tlsConfiguration: null,
            SipTransportProtocol.Udp,
            routeResolver: null,
            new SipTransportOptions { MaxInboundConnectionsPerRemote = 1 });
        var tcpPort = runtime.GetLocalEndPoint(SipTransportProtocol.Tcp).Port;

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, tcpPort);
        // Let the runtime accept and admit the first connection before the second races in.
        await Task.Delay(250);

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, tcpPort);

        // The second connection from the same source IP exceeds the per-remote cap and is dropped.
        Assert.Equal(0, await ReadTolerantAsync(second.GetStream()));
    }

    [Fact]
    public async Task A_tls_client_that_never_completes_the_handshake_is_dropped_after_the_deadline()
    {
        var pfxPath = Path.GetTempFileName();
        const string password = "p13-slowloris-pw";
        try
        {
            using (var cert = CreateSelfSigned("CN=sip-tls-slowloris"))
                await File.WriteAllBytesAsync(pfxPath, cert.Export(X509ContentType.Pkcs12, password));

            using var runtime = new SipTransportRuntime(
                NullLoggerFactory.Instance,
                new SipWireProtocol(),
                new TlsConfiguration { CertificatePath = pfxPath, CertificatePassword = password },
                SipTransportProtocol.Udp,
                routeResolver: null,
                new SipTransportOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });

            var tlsEndPoint = runtime.GetLocalEndPoint(SipTransportProtocol.Tls);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, tlsEndPoint.Port);

            // Connect but never send a TLS ClientHello: the server-side AuthenticateAsServer must hit the
            // handshake deadline, drop the connection and free its admission slot. A read then returns
            // 0/reset.
            Assert.Equal(0, await ReadTolerantAsync(client.GetStream()));
        }
        finally
        {
            File.Delete(pfxPath);
        }
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
