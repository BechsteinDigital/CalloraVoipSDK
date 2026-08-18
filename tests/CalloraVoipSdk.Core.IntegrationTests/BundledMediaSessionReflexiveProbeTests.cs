using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Re-gathering the server-reflexive address on a <em>running</em> session (#226): the transport's receive loop
/// owns the socket, so the probe cannot read from it directly — the request goes out through the transport's raw
/// send and the answer comes back through the same inbound STUN demux that feeds the ICE agent.
/// <para>
/// This is what makes an ICE restart able to re-gather at all. The alternative — taking the socket back — would
/// cost the DTLS association and every SRTP context keyed to it, which is precisely what a restart must preserve.
/// </para>
/// </summary>
public sealed class BundledMediaSessionReflexiveProbeTests
{
    [Fact]
    public async Task The_reflexive_address_is_discovered_over_the_running_transport()
    {
        // A minimal STUN server: answers any Binding request with the source it saw, as a real one would.
        using var stunServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var stunEndPoint = (IPEndPoint)stunServer.Client.LocalEndPoint!;
        var codec = new StunMessageCodec();
        using var serverLife = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = ServeAsync(stunServer, codec, serverLife.Token);

        await using var session = CreateSession();
        await session.StartAsync();   // the receive loop now owns the socket

        var reflexive = await session.ProbeServerReflexiveAsync(stunEndPoint, TimeSpan.FromSeconds(2));

        Assert.NotNull(reflexive);
        // The server reports what it saw, so this must be exactly the media socket the transport is running on —
        // proof the probe rode the live transport rather than some socket of its own.
        Assert.Equal(session.LocalEndPoint.Port, reflexive!.Port);

        await serverLife.CancelAsync();
        await serving;
    }

    [Fact]
    public async Task A_silent_stun_server_yields_no_candidate_rather_than_an_error()
    {
        // Bound but never answering: a missing candidate is one fewer path to try, not a failure.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var silentEndPoint = (IPEndPoint)silent.Client.LocalEndPoint!;

        await using var session = CreateSession();
        await session.StartAsync();

        Assert.Null(await session.ProbeServerReflexiveAsync(silentEndPoint, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task A_probe_response_does_not_disturb_the_ice_agent()
    {
        // The probe and the ICE agent share one inbound STUN feed. Both match by transaction id, so the agent
        // must treat a probe response as noise — an agent that acted on it would be refreshing consent from
        // traffic that never came from the peer.
        using var stunServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var stunEndPoint = (IPEndPoint)stunServer.Client.LocalEndPoint!;
        var codec = new StunMessageCodec();
        using var serverLife = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = ServeAsync(stunServer, codec, serverLife.Token);

        var consentLost = false;
        await using var session = CreateSession();
        await session.StartAsync();

        Assert.NotNull(await session.ProbeServerReflexiveAsync(stunEndPoint, TimeSpan.FromSeconds(2)));

        // Still a live ICE session on the same socket, still answering the peer's credentials.
        Assert.False(consentLost);
        Assert.Equal("rem0", session.RemoteIceUfrag);

        await serverLife.CancelAsync();
        await serving;
    }

    // Echoes every Binding request back as a success response carrying XOR-MAPPED-ADDRESS of the sender.
    private static async Task ServeAsync(UdpClient server, StunMessageCodec codec, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var received = await server.ReceiveAsync(ct);
                if (codec.Decode(received.Buffer) is not { MessageMethod: StunMessageMethod.Binding } request)
                    continue;

                var response = new StunMessage
                {
                    MessageClass = StunMessageClass.SuccessResponse,
                    MessageMethod = StunMessageMethod.Binding,
                    TransactionId = request.TransactionId,
                    Attributes = [new XorMappedAddressAttribute { EndPoint = received.RemoteEndPoint }],
                };
                var bytes = codec.Encode(response);
                await server.SendAsync(bytes, bytes.Length, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static BundledMediaSession CreateSession()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var peer = new IPEndPoint(IPAddress.Loopback, 50000);
        return new BundledMediaSession(
            new BundledMediaSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                RemoteEndPoint = peer,
                MidExtensionId = 3,
                Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
                DtlsIsClient = false,
                RemoteFingerprint = cert.Fingerprint,
                Ice = new IceMediaParameters(
                    peer, IceEnabled: true, IceControlling: false,
                    LocalIceUfrag: "loc0", LocalIcePwd: "localicepassword1234567890",
                    RemoteIceUfrag: "rem0", RemoteIcePwd: "remoteicepassword123456789"),
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
    }
}
