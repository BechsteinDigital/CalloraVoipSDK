using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The stream relay's inbound wiring into the ICE agent (ADR-073 slice 4c-ii, #240). A stream relay owns its own
/// receive loop, distinct from the shared media socket, so its unwrapped relayed connectivity checks reach the
/// agent through <see cref="BundledIceControl.OnRelayStunReceived"/> rather than the inbound pipeline. This proves
/// the two directions of that seam: an inbound relayed <em>response</em> confirms the relay candidate's check so
/// the controlling agent nominates the relay pair, and an inbound relayed <em>request</em> is answered back
/// through the relay reply path (RFC 8656 §10) — never over the direct socket.
/// </summary>
public sealed class BundledIceControlStreamRelayTests
{
    [Fact]
    public async Task Relay_pair_is_nominated_when_checks_are_answered_via_OnRelayStunReceived()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 53101);
        var pipeline = Pipeline();

        var relayChecks = 0;
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        BundledIceControl control = null!;

        // The direct media socket is a black hole (an unreachable socket throws on send), so the higher-priority
        // host pair is abandoned and the driver falls through to the relay pair.
        ValueTask DirectSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
            => throw new SocketException((int)SocketError.NetworkUnreachable);

        // The stream relay's send path echoes each check's transaction id back as a Binding Success Response —
        // but delivered the stream way: through OnRelayStunReceived (its own receive loop), not the pipeline. That
        // confirms the relay candidate's check via consent, so the relay pair validates and is nominated.
        ValueTask RelaySend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        {
            Interlocked.Increment(ref relayChecks);
            var response = new byte[20];
            response[0] = 0x01; response[1] = 0x01;              // Binding Success Response
            response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42;  // RFC 5389 magic cookie
            datagram.Span.Slice(8, 12).CopyTo(response.AsSpan(8)); // echo the transaction id so consent matches
            _ = Task.Run(() => control.OnRelayStunReceived(response, target, RelaySend));
            return ValueTask.CompletedTask;
        }

        var iceParameters = new IceMediaParameters(
            remote, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(remote, Priority: 100)],
        };

        control = new BundledIceControl(
            iceParameters, pipeline, DirectSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep),
            relaySend: RelaySend);
        try
        {
            control.Start();

            var picked = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(remote, picked);
            Assert.True(relayChecks >= 1, "the relay send path must have been exercised and its response fed via OnRelayStunReceived");
        }
        finally
        {
            await control.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_inbound_relayed_check_is_answered_back_through_the_relay_reply_path()
    {
        var peer = new IPEndPoint(IPAddress.Loopback, 53102);
        var pipeline = Pipeline();

        // The direct socket is dead: if the response leaked onto it instead of the relay reply path, it would
        // throw — so a reply observed on replyVia proves the inbound relayed check was answered the way it came.
        ValueTask DirectSend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
            => throw new SocketException((int)SocketError.NetworkUnreachable);

        var iceParameters = new IceMediaParameters(
            peer, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(peer, Priority: 100)],
        };

        await using var control = new BundledIceControl(
            iceParameters, pipeline, DirectSend, NullLoggerFactory.Instance);
        control.Start();

        var repliedTo = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask ReplyVia(ReadOnlyMemory<byte> response, IPEndPoint dest, CancellationToken ct)
        {
            repliedTo.TrySetResult(dest);
            return ValueTask.CompletedTask;
        }

        // Craft the peer's inbound Binding Request as the answerer would send it: USERNAME "offr:answ" and
        // MESSAGE-INTEGRITY keyed with our local password ("offrPassword"), ICE-CONTROLLED. Build produces
        // USERNAME "{remoteUfrag}:{localUfrag}" keyed with remotePassword, so the args are swapped accordingly.
        var (request, _) = IceConsentCheckBuilder.Build(
            new StunMessageCodec(),
            localUfrag: "answ", remoteUfrag: "offr", remotePassword: "offrPassword",
            priority: 100, controlling: false, tieBreaker: 0x1122334455667788UL, useCandidate: false);

        control.OnRelayStunReceived(request, peer, ReplyVia);

        // The inbound triggered check is answered back through the relay reply path, addressed to the peer.
        Assert.Equal(peer, await repliedTo.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static BundledInboundPipeline Pipeline()
    {
        var demux = BundledRtpDemultiplexerFactory.Create(3, new Dictionary<string, IReadOnlyCollection<int>>());
        var router = new BundledTrackRouter(demux);
        return new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
    }
}
