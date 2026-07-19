using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end wiring of <see cref="TurnRelayCoordinator"/> over the real <see cref="BundledMediaTransport"/>:
/// the coordinator sends TURN requests through the transport's control path and is fed responses back through
/// the transport's relay-control callback, drives Allocate → CreatePermission → ChannelBind against a fake
/// TURN server socket, then installs the bound channel so the data path activates. This proves the whole
/// relay control stack composes over the two-phase transport (the payoff of the 4c series).
/// </summary>
public sealed class TurnRelayCoordinatorTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const ushort ChannelNumber = 0x4001;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task EstablishAsync_completes_the_handshake_over_the_transport_and_activates_the_data_path()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        var relayedAddress = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 49152);
        var channelDataObserved = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, relayedAddress, channelBindSucceeds: true, channelDataObserved, serverCts.Token);

        var peer = new IPEndPoint(IPAddress.Loopback, 9999);
        TurnRelayCoordinator relay = null!;
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions
            {
                LocalEndPoint = Loopback(),
                RemoteEndPoint = peer,
                RelayServer = serverEndPoint,
                OnRelayControl = m => relay.OnControlDatagram(m),
            },
            InboundPipeline(), NullLogger<BundledMediaTransport>.Instance);
        relay = new TurnRelayCoordinator(transport, serverEndPoint, codec, NullLogger<TurnRelayCoordinator>.Instance);
        await transport.StartAsync();

        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };
        var allocation = await relay
            .EstablishAsync(peer, ChannelNumber, credentials, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(relayedAddress, allocation.RelayedEndPoint);
        Assert.Equal(600u, allocation.LifetimeSeconds);
        Assert.Equal(ChannelNumber, Assert.IsType<TurnRelayChannel>(allocation.Channel).ChannelNumber);
        Assert.Equal("callora", allocation.EffectiveCredentials?.Realm);

        // The data path is now active: outbound media is framed as ChannelData for the bound channel.
        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), transport, NullLogger<BundledOutboundPipeline>.Instance);
        outbound.RegisterTrack("audio", Track());
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });

        Assert.Equal(ChannelNumber, await channelDataObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        serverCts.Cancel();
        await serverLoop;
    }

    [Fact]
    public async Task EstablishAsync_leaves_the_data_path_suppressed_when_a_step_fails()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        var relayedAddress = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 49152);
        var channelDataObserved = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, relayedAddress, channelBindSucceeds: false, channelDataObserved, serverCts.Token);

        var peer = new IPEndPoint(IPAddress.Loopback, 9999);
        TurnRelayCoordinator relay = null!;
        await using var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions
            {
                LocalEndPoint = Loopback(),
                RemoteEndPoint = peer,
                RelayServer = serverEndPoint,
                OnRelayControl = m => relay.OnControlDatagram(m),
            },
            InboundPipeline(), NullLogger<BundledMediaTransport>.Instance);
        relay = new TurnRelayCoordinator(transport, serverEndPoint, codec, NullLogger<TurnRelayCoordinator>.Instance);
        await transport.StartAsync();

        var credentials = new StunCredentials { Username = "user", Password = "pass", Realm = "bootstrap" };
        await Assert.ThrowsAsync<TurnException>(() => relay
            .EstablishAsync(peer, ChannelNumber, credentials, lifetimeSeconds: 600, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        // The channel-bind failed, so SetRelayChannel was never called: outbound media stays suppressed.
        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), transport, NullLogger<BundledOutboundPipeline>.Instance);
        outbound.RegisterTrack("audio", Track());
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        await outbound.SendAsync("audio", new byte[] { 1, 2, 3 });

        await Task.Delay(300);
        Assert.False(channelDataObserved.Task.IsCompleted);

        serverCts.Cancel();
        await serverLoop;
    }

    // ── fake TURN server ─────────────────────────────────────────────────────────

    private static async Task RunFakeTurnServerAsync(
        UdpClient server,
        IStunMessageCodec codec,
        IPEndPoint relayedAddress,
        bool channelBindSucceeds,
        TaskCompletionSource<ushort> channelDataObserved,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await server.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (TurnChannelDataCodec.TryParse(received.Buffer, out var channel, out _))
            {
                channelDataObserved.TrySetResult(channel);
                continue;
            }

            var request = codec.Decode(received.Buffer);
            if (request is null)
                continue;

            var method = (TurnMessageMethod)(ushort)request.MessageMethod;
            var response = method switch
            {
                TurnMessageMethod.Allocate when !HasUsername(request) =>
                    Error(codec, request, 401, "Unauthorized",
                        new RealmAttribute { Value = "callora" }, new NonceAttribute { Value = "nonce-1" }),
                TurnMessageMethod.Allocate => AllocateSuccess(codec, request, relayedAddress, 600),
                TurnMessageMethod.CreatePermission => EmptySuccess(codec, request),
                TurnMessageMethod.ChannelBind when channelBindSucceeds => EmptySuccess(codec, request),
                TurnMessageMethod.ChannelBind => Error(codec, request, 486, "Allocation Quota Reached"),
                _ => EmptySuccess(codec, request),
            };

            await server.SendAsync(response, response.Length, (IPEndPoint)received.RemoteEndPoint);
        }
    }

    private static bool HasUsername(StunMessage message) => message.Attributes.OfType<UsernameAttribute>().Any();

    private static byte[] Error(IStunMessageCodec codec, StunMessage req, int code, string reason, params StunAttribute[] extra)
    {
        var attributes = new List<StunAttribute> { new ErrorCodeAttribute { Code = code, Reason = reason } };
        attributes.AddRange(extra);
        return codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.ErrorResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes = attributes,
        });
    }

    private static byte[] EmptySuccess(IStunMessageCodec codec, StunMessage req)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes = Array.Empty<StunAttribute>(),
        });

    private static byte[] AllocateSuccess(IStunMessageCodec codec, StunMessage req, IPEndPoint relayed, uint lifetimeSeconds)
        => codec.Encode(new StunMessage
        {
            MessageClass = StunMessageClass.SuccessResponse,
            MessageMethod = req.MessageMethod,
            TransactionId = req.TransactionId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorRelayedAddressAttribute { EndPoint = relayed }, req.TransactionId),
                TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds }),
            ],
        });

    // ── transport harness (mirrors BundledMediaTransportRelayTests) ──────────────

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 0);

    private static BundledInboundPipeline InboundPipeline()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>> { ["audio"] = new[] { (int)AudioPayloadType } });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", _ => { });
        var pipeline = new BundledInboundPipeline(
            router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        return pipeline;
    }

    private static BundledOutboundTrack Track() =>
        new(AudioSsrc, AudioPayloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "audio"),
            initialSequenceNumber: 1000, initialTimestamp: 5000);

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };
}
