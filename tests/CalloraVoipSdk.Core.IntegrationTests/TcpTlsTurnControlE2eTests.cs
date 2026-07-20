using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end proof of the TURN control transport over TCP and TLS (RFC 8656 / RFC 5389 §7.2.2): the client
/// stack (<see cref="TurnClientTransport"/> via <see cref="TurnClient"/>) allocates a relay against a hosted
/// stream-transport TURN server (<see cref="TurnServerHost"/> with a TCP/TLS listener). This closes the
/// previously untested gap — the TCP/TLS control path was built and wired (the SIP allocator passes the
/// configured transport through) but had no end-to-end coverage. The relayed <em>data</em> path over a
/// stream transport is a separate, larger feature and is not exercised here.
/// </summary>
public sealed class TcpTlsTurnControlE2eTests
{
    [Fact]
    public async Task Turn_client_allocates_over_tcp_against_a_hosted_tcp_server()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tcp,
            RequireAuthentication = false,
        });
        host.Start();

        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);
        var allocation = await client.AllocateAsync(
            host.LocalEndPoint,
            credentials: null,
            options: null,
            transport: TurnTransport.Tcp);

        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        Assert.True(allocation.LifetimeSeconds > 0, "the TCP allocation must grant a positive lifetime");
    }

    [Fact]
    public async Task Turn_client_allocates_over_tls_against_a_hosted_tls_server()
    {
        using var certificate = SelfSignedTlsCertificate();
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tls,
            TlsCertificate = certificate,
            RequireAuthentication = false,
        });
        host.Start();

        var client = new TurnClient(new StunMessageCodec(), NullLogger<TurnClient>.Instance);
        var allocation = await client.AllocateAsync(
            host.LocalEndPoint,
            credentials: null,
            options: null,
            transport: TurnTransport.Tls,
            tlsTargetHost: "localhost",
            tlsRemoteCertificateValidationCallback: (_, _, _, _) => true); // accept the self-signed test cert

        Assert.NotNull(allocation.RelayedEndPoint);
        Assert.NotEqual(0, allocation.RelayedEndPoint.Port);
        Assert.True(allocation.LifetimeSeconds > 0, "the TLS allocation must grant a positive lifetime");
    }

    private static X509Certificate2 SelfSignedTlsCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // CreateSelfSigned hands back a certificate whose private key is ephemeral. On Windows (SChannel) such a
        // key cannot back a server-side SslStream handshake — AuthenticateAsServerAsync aborts and the client
        // observes an unexpected EOF — whereas on Linux (OpenSSL) it works directly. Round-tripping through a PFX
        // export/import associates the key with a container SChannel accepts for server authentication, which is
        // also the shape a real deployment gets when it loads its certificate from a PFX or the certificate store.
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
        return new X509Certificate2(pfx);
#endif
    }
}
