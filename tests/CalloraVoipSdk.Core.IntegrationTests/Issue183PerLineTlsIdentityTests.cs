using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #183 (slice 3a): per-line TLS identity reaches the wire through the real
/// <see cref="SipTransportRuntime"/>. Two lines sending to the SAME registrar endpoint each present
/// their OWN client certificate over distinct pooled connections, and each line's RFC 5922
/// server-trust policy (DECISION D) is applied fail-closed to that line's connection.
/// </summary>
public sealed class Issue183PerLineTlsIdentityTests
{
    [Fact]
    public async Task Two_lines_present_their_own_client_certificate_to_the_same_endpoint()
    {
        using var serverCert = SelfSignedServer("sip.shared.example");
        using var certA = SelfSignedClient("line-a");
        using var certB = SelfSignedClient("line-b");
        await using var server = new TlsSipCaptureServer(serverCert);
        using var runtime = NewRuntime();

        var lineA = new TlsConfiguration { ClientCertificate = certA, TrustMode = SipTlsTrustMode.DangerousAcceptAnyChain };
        var lineB = new TlsConfiguration { ClientCertificate = certB, TrustMode = SipTlsTrustMode.DangerousAcceptAnyChain };

        await SendOptionsAsync(runtime, server.EndPoint, lineA);
        await SendOptionsAsync(runtime, server.EndPoint, lineB);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = await server.WaitForClientThumbprintsAsync(2, cts.Token);

        Assert.Equal(
            new[] { certA.Thumbprint, certB.Thumbprint }.OrderBy(t => t, StringComparer.Ordinal),
            seen.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Per_line_expected_sip_domain_is_enforced_fail_closed()
    {
        using var serverCert = SelfSignedServer("sip.line-a.example");
        using var certA = SelfSignedClient("line-a");
        await using var server = new TlsSipCaptureServer(serverCert);
        using var runtime = NewRuntime();

        // Matching RFC 5922 domain → the handshake completes and the line's certificate is presented.
        var matching = new TlsConfiguration
        {
            ClientCertificate = certA,
            TrustMode = SipTlsTrustMode.DangerousAcceptAnyChain,
            ExpectedSipDomain = "sip.line-a.example"
        };
        await SendOptionsAsync(runtime, server.EndPoint, matching);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(certA.Thumbprint, Assert.Single(await server.WaitForClientThumbprintsAsync(1, cts.Token)));

        // Mismatching RFC 5922 domain → fail-closed even under DangerousAcceptAnyChain: the send fails.
        var mismatching = new TlsConfiguration
        {
            ClientCertificate = certA,
            TrustMode = SipTlsTrustMode.DangerousAcceptAnyChain,
            ExpectedSipDomain = "sip.evil.example"
        };
        await Assert.ThrowsAnyAsync<Exception>(() => SendOptionsAsync(runtime, server.EndPoint, mismatching));
    }

    private static async Task SendOptionsAsync(SipTransportRuntime runtime, IPEndPoint endpoint, TlsConfiguration lineTls)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/TLS 127.0.0.1;branch=z9hG4bK-issue183",
            ["CSeq"] = "1 OPTIONS",
            ["Content-Length"] = "0"
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await runtime.SendRequestAsync(
            "OPTIONS", "sip:127.0.0.1", headers, body: null, endpoint,
            SipTransportProtocol.Tls, lineTls, cts.Token);
    }

    private static SipTransportRuntime NewRuntime()
        => new(NullLoggerFactory.Instance, new SipWireProtocol(), tlsConfiguration: null, SipTransportProtocol.Udp, routeResolver: null);

    private static X509Certificate2 SelfSignedClient(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static X509Certificate2 SelfSignedServer(string dnsSan)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=issue183-server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // A real SIP domain server certificate carries TLS serverAuth plus RFC 5924 §5 id-kp-sipDomain
        // (1.3.6.1.5.5.7.3.20); the latter is what SipDomainCertificateValidator requires.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.20") }, critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsSan);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>
    /// Minimal loopback TLS SIP server that requests a client certificate and publishes the SHA-1
    /// thumbprint each accepted connection presented (or none). Drains the connection so the client's
    /// payload send completes and the pooled connection stays alive.
    /// </summary>
    private sealed class TlsSipCaptureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _serverCert;
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<string?> _thumbprints = Channel.CreateUnbounded<string?>();
        private readonly Task _acceptLoop;

        public TlsSipCaptureServer(X509Certificate2 serverCert)
        {
            _serverCert = serverCert;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public async Task<IReadOnlyList<string?>> WaitForClientThumbprintsAsync(int count, CancellationToken ct)
        {
            var result = new List<string?>(count);
            for (var i = 0; i < count; i++)
                result.Add(await _thumbprints.Reader.ReadAsync(ct));
            return result;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleConnectionAsync(client, ct);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await using (ssl.ConfigureAwait(false))
                {
                    string? thumbprint = null;
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _serverCert,
                                ClientCertificateRequired = true,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                                {
                                    thumbprint = cert?.GetCertHashString();
                                    return true;
                                }
                            },
                            ct);
                    }
                    catch (Exception)
                    {
                        // The client aborted the handshake (e.g. it rejected our server certificate under a
                        // mismatching RFC 5922 domain). No client identity to record for this connection.
                        return;
                    }

                    await _thumbprints.Writer.WriteAsync(thumbprint, ct);

                    var buffer = new byte[1024];
                    try
                    {
                        while (await ssl.ReadAsync(buffer, ct) > 0)
                        {
                            // Drain and discard the SIP payload; keeps the pooled connection open.
                        }
                    }
                    catch (Exception)
                    {
                        // Connection closed or the server was cancelled; nothing more to drain.
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
                // Accept loop faulted during teardown; irrelevant to the test outcome.
            }

            _cts.Dispose();
        }
    }
}
