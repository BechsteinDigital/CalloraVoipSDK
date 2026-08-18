using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE restart on a running <see cref="BundledMediaSession"/> (#226, RFC 8445 §9 / RFC 8839 §5.4): the peer
/// rotates its ICE credentials mid-session and the session re-runs connectivity checks against them —
/// <em>without</em> rebuilding the transport. These assert the session half: that the shared socket survives, that
/// the new credentials are answered, and that the credentials the renegotiator reads back follow the restart
/// (reading them from the construction-time options would report a restart against every later re-offer).
/// </summary>
public sealed class BundledMediaSessionIceRestartTests
{
    private const string LocalUfrag = "loc0";
    private const string LocalPwd = "localicepassword1234567890";
    private const string PeerUfrag = "rem0";
    private const string PeerPwd = "remoteicepassword123456789";

    private const string NewLocalUfrag = "loc1";
    private const string NewLocalPwd = "restartedlocalpassword1234";
    private const string NewPeerUfrag = "rem1";
    private const string NewPeerPwd = "restartedremotepassword123";

    [Fact]
    public async Task An_ice_restart_keeps_the_socket_and_answers_the_rotated_credentials()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        await using var session = CreateSession(peerEndPoint);
        await session.StartAsync();

        var socketBefore = session.LocalEndPoint;
        Assert.Equal(PeerUfrag, session.RemoteIceUfrag);

        await session.RestartIceAsync(Parameters(peerEndPoint, NewLocalUfrag, NewLocalPwd, NewPeerUfrag, NewPeerPwd));

        // The whole point of a restart over a rebuild: the same 5-tuple, so the DTLS association and every
        // per-SSRC SRTP context on it stay valid. A new port here would mean the peer had to re-handshake.
        Assert.Equal(socketBefore, session.LocalEndPoint);
        Assert.Equal(NewPeerUfrag, session.RemoteIceUfrag);
        Assert.Equal(NewPeerPwd, session.RemoteIcePwd);

        var codec = new StunMessageCodec();
        var (check, transactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: NewPeerUfrag, remoteUfrag: NewLocalUfrag, remotePassword: NewLocalPwd,
            priority: 12345u, controlling: true, tieBreaker: 42);
        await peer.SendAsync(check, session.LocalEndPoint);

        Assert.True(
            await AwaitSuccessResponseAsync(peer, codec, transactionId, TimeSpan.FromSeconds(5)),
            "The running session did not answer the check carrying the rotated credentials.");
    }

    [Fact]
    public async Task After_an_ice_restart_the_retired_credentials_are_no_longer_answered()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        await using var session = CreateSession(peerEndPoint);
        await session.StartAsync();

        await session.RestartIceAsync(Parameters(peerEndPoint, NewLocalUfrag, NewLocalPwd, NewPeerUfrag, NewPeerPwd));

        // Exactly the check that would have been answered before the restart.
        var codec = new StunMessageCodec();
        var (staleCheck, staleTransactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: PeerUfrag, remoteUfrag: LocalUfrag, remotePassword: LocalPwd,
            priority: 12345u, controlling: true, tieBreaker: 42);
        await peer.SendAsync(staleCheck, session.LocalEndPoint);

        // Not "no datagram arrives" — the session's own consent checks go to this socket, which is how we know it
        // is alive. What must not arrive is an answer to the retired transaction.
        Assert.False(
            await AwaitSuccessResponseAsync(peer, codec, staleTransactionId, TimeSpan.FromSeconds(2)),
            "A retired credential pair was still answered after the restart.");
    }

    [Fact]
    public async Task Restarting_a_disposed_session_is_rejected()
    {
        var peerEndPoint = new IPEndPoint(IPAddress.Loopback, 50000);
        var session = CreateSession(peerEndPoint);
        await session.StartAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.RestartIceAsync(Parameters(peerEndPoint, NewLocalUfrag, NewLocalPwd, NewPeerUfrag, NewPeerPwd)));
    }

    // A controlled (answering) session on a loopback socket: DtlsIsClient false so nothing initiates a handshake
    // against the test peer socket, leaving only ICE traffic on the wire.
    private static BundledMediaSession CreateSession(IPEndPoint peerEndPoint)
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        var options = new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RemoteEndPoint = peerEndPoint,
            MidExtensionId = 3,
            Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, SamplesPerPacket = 160 },
            DtlsIsClient = false,
            RemoteFingerprint = cert.Fingerprint,
            Ice = Parameters(peerEndPoint, LocalUfrag, LocalPwd, PeerUfrag, PeerPwd),
        };

        return new BundledMediaSession(
            options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);
    }

    private static IceMediaParameters Parameters(
        IPEndPoint remote, string localUfrag, string localPwd, string remoteUfrag, string remotePwd) =>
        new(remote, IceEnabled: true, IceControlling: false,
            LocalIceUfrag: localUfrag, LocalIcePwd: localPwd,
            RemoteIceUfrag: remoteUfrag, RemoteIcePwd: remotePwd)
        {
            RemoteCandidates = [new IceRemoteCandidate(remote, Priority: 100)],
        };

    // Drains the peer socket until a success response for this transaction arrives or the deadline passes;
    // everything else on the wire is the session's own outbound ICE traffic.
    private static async Task<bool> AwaitSuccessResponseAsync(
        UdpClient peer, StunMessageCodec codec, byte[] transactionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var receive = peer.ReceiveAsync();
            var completed = await Task.WhenAny(receive, Task.Delay(deadline - DateTime.UtcNow));
            if (completed != receive)
                return false;

            var message = codec.Decode(receive.Result.Buffer);
            if (message is { MessageClass: StunMessageClass.SuccessResponse, MessageMethod: StunMessageMethod.Binding }
                && message.TransactionId.AsSpan().SequenceEqual(transactionId))
            {
                return true;
            }
        }

        return false;
    }
}
