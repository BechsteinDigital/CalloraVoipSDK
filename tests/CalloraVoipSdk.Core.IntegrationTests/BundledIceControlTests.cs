using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE on a bundled transport (ADR-011 B3-3, RFC 8445/7675): one ICE agent over the shared 5-tuple.
/// The test drives a real ICE connectivity check into the bundle socket and shows it is answered over
/// the same socket — proving STUN demuxed by the inbound pipeline reaches the ICE attachment and its
/// response goes back out through the transport's targeted send.
/// </summary>
public sealed class BundledIceControlTests
{
    private const string BundleUfrag = "bnd0";
    private const string BundlePwd = "bundlepassword1234567890";
    private const string PeerUfrag = "peer";
    private const string PeerPwd = "peerpassword1234567890";

    [Fact]
    public async Task Inbound_ice_connectivity_check_is_answered_over_the_bundle_socket()
    {
        // Bind the peer first so it is the nominated remote — then its check is not seen as a
        // peer-reflexive source and no RFC 8445 §7.3.1.4 triggered check races the success response.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var inbound = InboundPipeline();
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            inbound, NullLogger<BundledMediaTransport>.Instance);

        var iceParameters = new IceMediaParameters(
            RemoteEndPoint: peerEndPoint,
            IceEnabled: true, IceControlling: false,
            LocalIceUfrag: BundleUfrag, LocalIcePwd: BundlePwd,
            RemoteIceUfrag: PeerUfrag, RemoteIcePwd: PeerPwd);

        await using var ice = new BundledIceControl(
            iceParameters, inbound, transport.SendToAsync, NullLoggerFactory.Instance);
        Assert.True(ice.IsActive);

        await transport.StartAsync();

        // The peer sends an ICE connectivity check to the bundle socket. It is authenticated with the
        // bundle's local password and USERNAME "bnd0:peer", so the inbound handler answers it.
        var codec = new StunMessageCodec();
        var (check, transactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: PeerUfrag, remoteUfrag: BundleUfrag, remotePassword: BundlePwd,
            priority: 12345u, controlling: true, tieBreaker: 42);

        await peer.SendAsync(check, transport.LocalEndPoint);

        var receiveTask = peer.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed); // a response came back over the shared socket

        var response = codec.Decode(receiveTask.Result.Buffer);
        Assert.NotNull(response);
        Assert.Equal(StunMessageClass.SuccessResponse, response!.MessageClass);
        Assert.Equal(StunMessageMethod.Binding, response.MessageMethod);
        Assert.Equal(transactionId, response.TransactionId);
    }

    [Fact]
    public async Task Without_ice_credentials_the_control_is_inactive()
    {
        var inbound = InboundPipeline();
        var iceParameters = new IceMediaParameters(
            RemoteEndPoint: new IPEndPoint(IPAddress.Loopback, 1),
            IceEnabled: false, IceControlling: false,
            LocalIceUfrag: null, LocalIcePwd: null, RemoteIceUfrag: null, RemoteIcePwd: null);

        await using var ice = new BundledIceControl(
            iceParameters, inbound, (_, _, _) => ValueTask.CompletedTask, NullLoggerFactory.Instance);

        Assert.False(ice.IsActive);
    }

    /// <summary>
    /// #226: an ICE restart (RFC 8445 §9) swaps the agent on a running transport. After it, a check
    /// authenticated with the <em>new</em> credentials is answered — the peer that rotated its ufrag can reach
    /// us again without the session being torn down and rebuilt.
    /// </summary>
    [Fact]
    public async Task After_an_ice_restart_a_check_with_the_new_credentials_is_answered()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var inbound = InboundPipeline();
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            inbound, NullLogger<BundledMediaTransport>.Instance);

        await using var ice = new BundledIceControl(
            Parameters(peerEndPoint, BundleUfrag, BundlePwd, PeerUfrag, PeerPwd),
            inbound, transport.SendToAsync, NullLoggerFactory.Instance);
        await transport.StartAsync();

        const string newBundleUfrag = "bnd1";
        const string newBundlePwd = "restartedpassword1234567";
        const string newPeerUfrag = "pee1";
        const string newPeerPwd = "restartedpeerpassword123";

        await ice.RestartIceAsync(Parameters(peerEndPoint, newBundleUfrag, newBundlePwd, newPeerUfrag, newPeerPwd));

        Assert.True(ice.IsActive); // the replacement agent is live, not a husk

        var codec = new StunMessageCodec();
        var (check, transactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: newPeerUfrag, remoteUfrag: newBundleUfrag, remotePassword: newBundlePwd,
            priority: 12345u, controlling: true, tieBreaker: 42);

        await peer.SendAsync(check, transport.LocalEndPoint);

        Assert.True(
            await AwaitSuccessResponseAsync(peer, codec, transactionId, TimeSpan.FromSeconds(5)),
            "The restarted agent did not answer the check carrying the new credentials.");
    }

    /// <summary>
    /// The other half, and the reason the old agent is detached before the new one attaches: once the restart
    /// has happened, a check carrying the retired credentials no longer authenticates. Two live agents would
    /// answer both, which is a protocol error and lets the retired check list nominate against the new one.
    /// </summary>
    [Fact]
    public async Task After_an_ice_restart_a_check_with_the_retired_credentials_is_not_answered()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        var inbound = InboundPipeline();
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            inbound, NullLogger<BundledMediaTransport>.Instance);

        await using var ice = new BundledIceControl(
            Parameters(peerEndPoint, BundleUfrag, BundlePwd, PeerUfrag, PeerPwd),
            inbound, transport.SendToAsync, NullLoggerFactory.Instance);
        await transport.StartAsync();

        await ice.RestartIceAsync(Parameters(peerEndPoint, "bnd1", "restartedpassword1234567", "pee1", "restartedpeerpassword123"));

        // Exactly the check that worked before the restart.
        var codec = new StunMessageCodec();
        var (staleCheck, staleTransactionId) = IceConsentCheckBuilder.Build(
            codec, localUfrag: PeerUfrag, remoteUfrag: BundleUfrag, remotePassword: BundlePwd,
            priority: 12345u, controlling: true, tieBreaker: 42);

        await peer.SendAsync(staleCheck, transport.LocalEndPoint);

        // Not "no datagram arrives" — the restarted agent sends its own checks to this socket, which is how we
        // know it is alive. What must not arrive is an answer to the retired one.
        Assert.False(
            await AwaitSuccessResponseAsync(peer, codec, staleTransactionId, TimeSpan.FromSeconds(2)),
            "A retired credential pair was still answered after the restart.");
    }

    // Drains the peer socket until a success response for this transaction arrives or the deadline passes.
    // Everything else on the wire is the agent's own outbound traffic (its consent and connectivity checks).
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

    private static IceMediaParameters Parameters(
        IPEndPoint remote, string localUfrag, string localPwd, string remoteUfrag, string remotePwd) =>
        new(RemoteEndPoint: remote,
            IceEnabled: true, IceControlling: false,
            LocalIceUfrag: localUfrag, LocalIcePwd: localPwd,
            RemoteIceUfrag: remoteUfrag, RemoteIcePwd: remotePwd);

    private static BundledInboundPipeline InboundPipeline()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            midExtensionId: 0,
            new Dictionary<string, IReadOnlyCollection<int>> { ["audio"] = new[] { 0 } });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", _ => { });
        return new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
    }
}
