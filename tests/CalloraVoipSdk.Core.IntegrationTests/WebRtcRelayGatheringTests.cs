using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE track slice 4d-2b (RFC 8656 relay gathering): the peer allocates a TURN relay on its pre-bound media
/// socket during <see cref="WebRtcPeerConnection.GatherCandidatesAsync"/> and emits the relayed address as a
/// relay ICE candidate (RFC 8445 §5.1.2 / RFC 8839) on the trickle event. The allocation is retained on the
/// peer so the relay coordinator can adopt it post-Start without re-allocating. A refused allocation, a
/// missing TURN probe, or a non-UDP TURN server yields no relay candidate (never a throw). Proven against a
/// fake TURN server over a real loopback socket.
/// </summary>
public sealed class WebRtcRelayGatheringTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IPEndPoint Relayed = new(IPAddress.Parse("198.51.100.9"), 49152);

    [Fact]
    public async Task GatherCandidates_emits_a_relay_candidate_and_retains_the_allocation()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, Relayed, succeed: true, serverCts.Token);

        await using var peer = Peer(TurnProbe(codec),
            [Turn(serverEndPoint.Port, username: "user", password: "pass")]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();                 // host candidate + binds the media socket
        await peer.GatherCandidatesAsync(); // TURN allocate on the media socket → relay candidate

        Assert.Contains(candidates, c => c.Contains("typ host", StringComparison.Ordinal));
        var relay = Assert.Single(candidates, c => c.Contains("typ relay", StringComparison.Ordinal));
        Assert.Contains("198.51.100.9 49152", relay, StringComparison.Ordinal); // the relayed transport address
        Assert.Contains("raddr", relay, StringComparison.Ordinal);              // carries the related base

        // The allocation is retained for the relay coordinator to adopt post-Start (same 5-tuple).
        var retained = peer.GatheredRelayAllocation;
        Assert.NotNull(retained);
        Assert.Equal(serverEndPoint, retained!.Value.ServerEndPoint);
        Assert.Equal(Relayed, retained.Value.Allocation.RelayedEndPoint);
        Assert.Equal("callora", retained.Value.Allocation.EffectiveCredentials?.Realm); // challenge realm/nonce kept
        Assert.Equal("nonce-1", retained.Value.Allocation.EffectiveCredentials?.Nonce);

        serverCts.Cancel();
        await serverLoop;
    }

    [Fact]
    public async Task GatherCandidates_when_the_allocation_is_refused_emits_no_relay_candidate()
    {
        var codec = new StunMessageCodec();
        using var fakeServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)fakeServer.Client.LocalEndPoint!;
        using var serverCts = new CancellationTokenSource();
        var serverLoop = RunFakeTurnServerAsync(fakeServer, codec, Relayed, succeed: false, serverCts.Token);

        await using var peer = Peer(TurnProbe(codec),
            [Turn(serverEndPoint.Port, username: "user", password: "pass")]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();
        await peer.GatherCandidatesAsync();

        Assert.DoesNotContain(candidates, c => c.Contains("typ relay", StringComparison.Ordinal));
        Assert.Null(peer.GatheredRelayAllocation); // a refused allocation surfaces as no candidate, not a throw

        serverCts.Cancel();
        await serverLoop;
    }

    [Fact]
    public async Task GatherCandidates_without_a_turn_probe_is_a_no_op()
    {
        await using var peer = Peer(turnProbe: null, [Turn(port: 3478, username: "user", password: "pass")]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();
        await peer.GatherCandidatesAsync(); // no probe → nothing to gather, no throw

        Assert.DoesNotContain(candidates, c => c.Contains("typ relay", StringComparison.Ordinal));
        Assert.Null(peer.GatheredRelayAllocation);
    }

    [Fact]
    public async Task GatherCandidates_skips_a_non_udp_turn_server()
    {
        await using var peer = Peer(TurnProbe(new StunMessageCodec()),
            [new IceServerConfiguration
            {
                Type = IceServerType.Turn, Host = "127.0.0.1", Port = 3478,
                Transport = IceTransport.Tcp, Username = "user", Password = "pass",
            }]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();
        await peer.GatherCandidatesAsync(); // TCP TURN is not gathered over the UDP media socket → no relay

        Assert.DoesNotContain(candidates, c => c.Contains("typ relay", StringComparison.Ordinal));
        Assert.Null(peer.GatheredRelayAllocation);
    }

    [Fact]
    public async Task GatherCandidates_after_start_throws()
    {
        await using var peer = Peer(TurnProbe(new StunMessageCodec()),
            [Turn(port: 3478, username: "user", password: "pass")]);

        await peer.SetRemoteDescriptionAsync(WebRtcOffer());
        await peer.StartAsync(); // the transport receive loop now owns the media socket

        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.GatherCandidatesAsync());
    }

    private static TurnAllocationProbe TurnProbe(IStunMessageCodec codec) =>
        new(codec, NullLoggerFactory.Instance, gatheringTimeout: TimeSpan.FromSeconds(5));

    private static IceServerConfiguration Turn(int port, string username, string password) => new()
    {
        Type = IceServerType.Turn, Host = "127.0.0.1", Port = port,
        Transport = IceTransport.Udp, Username = username, Password = password,
    };

    // A minimal remote WebRTC offer (BUNDLE + DTLS + ICE) so SetRemoteDescription builds a session.
    private static string WebRtcOffer() => new SdpSessionSerializer().Serialize(
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
            }));

    private static WebRtcPeerConnection Peer(
        TurnAllocationProbe? turnProbe,
        IReadOnlyList<IceServerConfiguration> iceServers) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
                IceServers = iceServers,
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance, stunProbe: null, turnProbe);

    // A fake TURN server (UDP): rejects with 400 when succeed is false; else drives the long-term auth
    // challenge (401 realm/nonce on the first, unauthenticated Allocate) and returns a relayed address on
    // the authenticated retry. Mirrors the harness used by TurnAllocationProbeTests.
    private static async Task RunFakeTurnServerAsync(
        UdpClient server, IStunMessageCodec codec, IPEndPoint relayed, bool succeed, CancellationToken ct)
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

            var request = codec.Decode(received.Buffer);
            if (request is null || (TurnMessageMethod)(ushort)request.MessageMethod != TurnMessageMethod.Allocate)
                continue;

            byte[] response;
            if (!succeed)
                response = Error(codec, request, 400, "Bad Request");
            else if (!HasUsername(request))
                response = Error(codec, request, 401, "Unauthorized",
                    new RealmAttribute { Value = "callora" }, new NonceAttribute { Value = "nonce-1" });
            else
                response = AllocateSuccess(codec, request, relayed, 600);

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
}
