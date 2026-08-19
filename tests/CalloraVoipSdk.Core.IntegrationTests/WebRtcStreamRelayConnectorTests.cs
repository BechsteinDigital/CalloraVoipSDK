using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using CalloraVoipSdk.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The config-to-candidate layer for a stream relay (ADR-073 slice 4c-iii-b, #240):
/// <see cref="WebRtcStreamRelayConnector"/> connects a persistent TCP/TLS stream to a configured TURN server and
/// gathers a stream relay candidate over it, against a real hosted <see cref="TurnServerHost"/>. A non-stream
/// transport or an unreachable server yields no candidate, never a throw.
/// </summary>
public sealed class WebRtcStreamRelayConnectorTests
{
    [Fact]
    public async Task Connects_over_tcp_and_gathers_a_working_stream_relay_candidate()
    {
        await using var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = IceTransport.Tcp,
            RequireAuthentication = false,
        });
        host.Start();

        var connector = new WebRtcStreamRelayConnector(new StunMessageCodec(), NullLoggerFactory.Instance);
        var server = new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = host.LocalEndPoint.Address.ToString(),
            Port = host.LocalEndPoint.Port,
            Transport = IceTransport.Tcp,
        };

        await using var candidate = await connector.ConnectAndGatherAsync(
            server, AddressFamily.InterNetwork, onInboundMedia: _ => { }, CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.NotEqual(0, candidate!.RelayedEndPoint.Port);

        // The connection is live: activate and drive the relay send path — a permission and a Send indication go
        // over the connected TCP stream and the control response rides back (completes without throwing).
        candidate.Activate(onInboundIndication: (_, _) => { });
        var peer = new IPEndPoint(IPAddress.Parse("198.51.100.30"), 50000);
        await candidate.Binding.RelaySend(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, peer, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Connects_over_tls_and_gathers_a_stream_relay_candidate()
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

        var connector = new WebRtcStreamRelayConnector(
            new StunMessageCodec(), NullLoggerFactory.Instance,
            tlsRemoteCertificateValidationCallback: (_, _, _, _) => true); // accept the self-signed test cert
        var server = new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = host.LocalEndPoint.Address.ToString(),
            Port = host.LocalEndPoint.Port,
            Transport = IceTransport.Tls,
        };

        await using var candidate = await connector.ConnectAndGatherAsync(
            server, AddressFamily.InterNetwork, onInboundMedia: _ => { }, CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.NotEqual(0, candidate!.RelayedEndPoint.Port);
    }

    [Fact]
    public async Task A_udp_turn_server_yields_no_stream_relay_candidate()
    {
        var connector = new WebRtcStreamRelayConnector(new StunMessageCodec(), NullLoggerFactory.Instance);
        var server = new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = "127.0.0.1",
            Port = 3478,
            Transport = IceTransport.Udp, // a stream relay is TCP/TLS only
        };

        var candidate = await connector.ConnectAndGatherAsync(
            server, AddressFamily.InterNetwork, onInboundMedia: _ => { }, CancellationToken.None);

        Assert.Null(candidate);
    }

    [Fact]
    public async Task An_unreachable_server_yields_no_candidate()
    {
        // Reserve then release a loopback port so nothing listens on it — a connect there is refused.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var connector = new WebRtcStreamRelayConnector(new StunMessageCodec(), NullLoggerFactory.Instance);
        var server = new IceServerConfiguration
        {
            Type = IceServerType.Turn,
            Host = "127.0.0.1",
            Port = deadPort,
            Transport = IceTransport.Tcp,
        };

        var candidate = await connector
            .ConnectAndGatherAsync(server, AddressFamily.InterNetwork, onInboundMedia: _ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(candidate);
    }

    private static X509Certificate2 SelfSignedTlsCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Round-trip through a PFX so the private key backs a server-side handshake on Windows SChannel too
        // (see TcpTlsTurnControlE2eTests for the full rationale).
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
        return new X509Certificate2(pfx);
#endif
    }
}
