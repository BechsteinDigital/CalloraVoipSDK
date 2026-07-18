using System.Net;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE track slice 2 (RFC 8445 §5.1.1 server-reflexive gathering): the peer queries a configured STUN
/// server through its pre-bound media socket and emits the discovered srflx candidate on the trickle event
/// (RFC 8838). Proven against a real loopback STUN server, where the reflexive endpoint equals the bound
/// media endpoint. No STUN server (or no probe) gathers host-only.
/// </summary>
public sealed class WebRtcSrflxGatheringTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task GatherCandidates_emits_a_server_reflexive_candidate_from_a_stun_server()
    {
        var codec = new StunMessageCodec();
        await using var stunServer = new StunServer(
            new IPEndPoint(IPAddress.Loopback, 0), codec, responseIntegrityKey: null, NullLogger<StunServer>.Instance);
        stunServer.Start(new StunBindingRequestHandler(codec, NullLogger<StunBindingRequestHandler>.Instance));

        var probe = new StunIceProbe(new StunClient(codec, NullLogger<StunClient>.Instance), NullLoggerFactory.Instance);
        await using var peer = Peer(probe,
            [new IceServerConfiguration { Type = IceServerType.Stun, Host = "127.0.0.1", Port = stunServer.LocalEndPoint.Port }]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();                 // host candidate + binds the media socket
        await peer.GatherCandidatesAsync(); // STUN query → srflx candidate

        Assert.Contains(candidates, c => c.Contains("typ host", StringComparison.Ordinal));
        var srflx = Assert.Single(candidates, c => c.Contains("typ srflx", StringComparison.Ordinal));
        Assert.Contains("raddr", srflx, StringComparison.Ordinal);   // carries the local base
    }

    [Fact]
    public async Task GatherCandidates_without_stun_servers_is_a_host_only_no_op()
    {
        var probe = new StunIceProbe(
            new StunClient(new StunMessageCodec(), NullLogger<StunClient>.Instance), NullLoggerFactory.Instance);
        await using var peer = Peer(probe, iceServers: []);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();
        await peer.GatherCandidatesAsync();

        Assert.DoesNotContain(candidates, c => c.Contains("typ srflx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GatherCandidates_without_a_probe_is_a_no_op()
    {
        await using var peer = Peer(stunProbe: null,
            [new IceServerConfiguration { Type = IceServerType.Stun, Host = "127.0.0.1", Port = 3478 }]);

        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += candidates.Add;

        peer.CreateOffer();
        await peer.GatherCandidatesAsync(); // no probe → nothing to gather, no throw

        Assert.DoesNotContain(candidates, c => c.Contains("typ srflx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GatherCandidates_after_start_throws()
    {
        var probe = new StunIceProbe(
            new StunClient(new StunMessageCodec(), NullLogger<StunClient>.Instance), NullLoggerFactory.Instance);
        await using var peer = Peer(probe,
            [new IceServerConfiguration { Type = IceServerType.Stun, Host = "127.0.0.1", Port = 3478 }]);

        await peer.SetRemoteDescriptionAsync(WebRtcOffer());
        await peer.StartAsync(); // the transport receive loop now owns the media socket

        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.GatherCandidatesAsync());
    }

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
        Core.Application.Ports.Connectivity.IIceStunProbe? stunProbe,
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
            NullLoggerFactory.Instance, stunProbe);
}
