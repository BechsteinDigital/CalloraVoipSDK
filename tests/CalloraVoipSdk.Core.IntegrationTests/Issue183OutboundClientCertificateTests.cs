using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (slice 1): the outbound SIP-TLS client handshake presents the configured identity
/// certificate for mutual TLS. A real loopback TLS handshake against a server that requests a client
/// certificate proves the certificate reaches the peer; with no certificate configured the handshake
/// still completes and the server observes none (behaviour-preserving).
/// </summary>
public sealed class Issue183OutboundClientCertificateTests
{
    [Fact]
    public async Task Outbound_handshake_presents_the_client_certificate_when_configured()
    {
        using var serverCert = SelfSigned("slice1-server", clientAuth: false);
        using var clientCert = SelfSigned("slice1-client", clientAuth: true);

        var seenThumbprint = await RunMutualHandshakeAsync(serverCert, clientCert);

        Assert.Equal(clientCert.Thumbprint, seenThumbprint);
    }

    [Fact]
    public async Task Outbound_handshake_presents_no_certificate_when_none_configured()
    {
        using var serverCert = SelfSigned("slice1-server", clientAuth: false);

        var seenThumbprint = await RunMutualHandshakeAsync(serverCert, clientCertificate: null);

        Assert.Null(seenThumbprint);
    }

    /// <summary>
    /// Drives one real loopback TLS handshake through <see cref="SipTransportRuntimeUtilities.AuthenticateOutboundTlsAsync"/>
    /// and returns the SHA-1 thumbprint of the client certificate the server received (or
    /// <see langword="null"/> when the client presented none).
    /// </summary>
    private static async Task<string?> RunMutualHandshakeAsync(
        X509Certificate2 serverCert,
        X509Certificate2? clientCertificate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var serverTask = AcceptAndCaptureClientThumbprintAsync(listener, serverCert, cts.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
            await using var clientSsl = await SipTransportRuntimeUtilities.AuthenticateOutboundTlsAsync(
                client.GetStream(),
                targetHost: "slice1-server",
                validateCertificate: (_, _, _, _) => true,
                cts.Token,
                clientCertificate);

            return await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string?> AcceptAndCaptureClientThumbprintAsync(
        TcpListener listener,
        X509Certificate2 serverCert,
        CancellationToken ct)
    {
        using var serverClient = await listener.AcceptTcpClientAsync(ct);
        string? capturedThumbprint = null;
        var serverSsl = new SslStream(serverClient.GetStream(), leaveInnerStreamOpen: false);
        await using (serverSsl.ConfigureAwait(false))
        {
            await serverSsl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    // Request a client certificate (mutual TLS). When the client presents one the
                    // callback captures its thumbprint; when it presents none the callback still runs
                    // with a null certificate and accepts, so the handshake completes either way.
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        capturedThumbprint = cert?.GetCertHashString();
                        return true;
                    }
                },
                ct).ConfigureAwait(false);
        }

        return capturedThumbprint;
    }

    private static X509Certificate2 SelfSigned(string commonName, bool clientAuth)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // 1.3.6.1.5.5.7.3.2 = TLS client auth; 1.3.6.1.5.5.7.3.1 = TLS server auth.
        var eku = new Oid(clientAuth ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1");
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { eku }, critical: false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return Issue183TestCertificates.WithUsablePrivateKey(ephemeral);
    }
}
