using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Regression for issue #7 (RFC 5766/8656 §10): with authentication enabled on the server, Send
/// indications are still relayed on a permission-only basis. Send/Data indications carry no
/// MESSAGE-INTEGRITY and must never be rejected for lacking it — only the requests (Allocate,
/// CreatePermission) are long-term authenticated. Before the fix the server required
/// MESSAGE-INTEGRITY on the indication and silently dropped RFC-conformant unsigned frames.
/// </summary>
public sealed class TurnServerIndicationAuthE2eTests
{
    private const string Realm = "callora.test";
    private const string Username = "alice";
    private const string Password = "s3cr3t";
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(3);

    private static TurnServer CreateAuthenticatedServer(IStunMessageCodec codec)
    {
        var authOptions = new TurnAuthOptions
        {
            Realm = Realm,
            CredentialProvider = new InMemoryStunCredentialProvider(
            [
                new StunCredentials { Username = Username, Password = Password, Realm = Realm }
            ]),
            NonceManager = new StunNonceManager()
        };

        var server = new TurnServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            TurnServerTransport.Udp,
            codec,
            NullLogger<TurnServer>.Instance,
            authOptions: authOptions,
            tlsServerCertificate: null,
            options: new TurnServerOptions { RequireAuthentication = true });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Unsigned_send_indication_is_relayed_when_server_requires_auth()
    {
        var codec = new StunMessageCodec();
        await using var server = CreateAuthenticatedServer(codec);
        using var client = new RawTurnUdpClient(server.LocalEndPoint, codec);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var credentials = new StunCredentials { Username = Username, Password = Password, Realm = Realm };

        // Requests are long-term authenticated (challenge → MESSAGE-INTEGRITY) ...
        var allocation = await client.AllocateAuthenticatedAsync(credentials);
        await client.CreatePermissionAuthenticatedAsync(credentials, peerEndPoint);

        // ... but the Send indication is unsigned (RFC 5766/8656 §10) and must still be relayed.
        var payload = "unsigned-indication"u8.ToArray();
        await client.SendIndicationAsync(peerEndPoint, payload);

        var received = await ReceiveWithTimeoutAsync(peer, RelayTimeout);
        Assert.Equal(payload, received.Buffer);
        // The relayed datagram arrives from the allocation's relayed address.
        Assert.Equal(allocation.RelayedEndPoint.Port, received.RemoteEndPoint.Port);
    }

    private static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await socket.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No datagram received within {timeout.TotalMilliseconds:F0} ms.");
        }
    }
}
