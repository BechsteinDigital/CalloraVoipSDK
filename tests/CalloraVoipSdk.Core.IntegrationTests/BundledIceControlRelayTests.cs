using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves <see cref="BundledIceControl"/> forwards an injected relay send path to its ICE agent as a relay
/// local candidate (RFC 8445 §5.1.1.2), and that a relayed connectivity check demuxed off the real
/// <see cref="BundledInboundPipeline"/> confirms it: when the direct path is dead, the controlling agent
/// nominates the relay pair. This is the ICE-side of the relay binding wired into the bundle transport stack.
/// </summary>
public sealed class BundledIceControlRelayTests
{
    [Fact]
    public async Task Relay_candidate_is_nominated_through_the_ice_control_when_the_direct_path_is_dead()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 53001);
        var pipeline = Pipeline();

        var relayChecks = 0;
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The direct media socket is a black hole (sends throw, as an unreachable socket would), so the
        // higher-priority host pair is abandoned and the driver falls through to the relay pair.
        ValueTask DirectSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
            => throw new SocketException((int)SocketError.NetworkUnreachable);

        // The relay send path answers each check by echoing its transaction id back through the real inbound
        // pipeline (a Binding Success Response header, 0x0101), so the relay pair validates and is nominated.
        ValueTask RelaySend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Interlocked.Increment(ref relayChecks);
            // A Binding Success Response (0x0101) with the RFC 5389 magic cookie so MediaPacketClassifier
            // routes it to the STUN handler, echoing the check's transaction id so consent matches it.
            var response = new byte[20];
            response[0] = 0x01;
            response[1] = 0x01;
            response[4] = 0x21;
            response[5] = 0x12;
            response[6] = 0xA4;
            response[7] = 0x42;
            datagram.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
            _ = Task.Run(() => pipeline.ProcessInboundDatagram(response, target));
            return ValueTask.CompletedTask;
        }

        var iceParameters = new IceMediaParameters(
            remote, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(remote, Priority: 100)],
        };

        await using var ice = new BundledIceControl(
            iceParameters, pipeline, DirectSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep),
            relaySend: RelaySend);
        ice.Start();

        var picked = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(remote, picked);
        Assert.True(relayChecks >= 1, "the injected relay send path must have been forwarded and exercised");
    }

    [Fact]
    public async Task Relay_candidate_adopted_after_construction_is_nominated_when_the_direct_path_is_dead()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 53002);
        var pipeline = Pipeline();

        var relayChecks = 0;
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The direct media socket is a black hole (an unreachable socket throws on send), so the higher-priority
        // host pair is abandoned and the driver falls through to the late-adopted relay pair.
        ValueTask DirectSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
            => throw new SocketException((int)SocketError.NetworkUnreachable);

        // The relay send path echoes each check's transaction id back through the real inbound pipeline as a
        // Binding Success Response (0x0101 + RFC 5389 magic cookie), so the relayed pair validates and nominates.
        ValueTask RelaySend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Interlocked.Increment(ref relayChecks);
            var response = new byte[20];
            response[0] = 0x01;
            response[1] = 0x01;
            response[4] = 0x21;
            response[5] = 0x12;
            response[6] = 0xA4;
            response[7] = 0x42;
            datagram.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
            _ = Task.Run(() => pipeline.ProcessInboundDatagram(response, target));
            return ValueTask.CompletedTask;
        }

        var iceParameters = new IceMediaParameters(
            remote, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(remote, Priority: 100)],
        };

        // Built direct-only (no relaySend at construction), like the answerer whose TURN allocation only gathers
        // after the session exists — then the relay candidate is adopted late.
        await using var ice = new BundledIceControl(
            iceParameters, pipeline, DirectSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep));
        ice.Start();
        ice.AddRelayLocalCandidate(RelaySend);

        var picked = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(remote, picked);
        Assert.True(relayChecks >= 1, "the late-adopted relay send path must have been checked");
    }

    private static BundledInboundPipeline Pipeline()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(3, new Dictionary<string, IReadOnlyCollection<int>>());
        var router = new BundledTrackRouter(demux);
        return new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
    }
}
