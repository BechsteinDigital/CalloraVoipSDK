using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Proves the controlling <see cref="IceMediaAttachment"/> adds a relay local candidate (RFC 8445 §5.1.1.2)
/// when a TURN-framed send path is injected, and that the driver nominates the relay pair only when the
/// higher-priority direct pair does not work — at which point consent freshness runs over the relay path.
/// The direct socket and the relay path are in-memory fakes, so the test is deterministic and socket-free.
/// </summary>
public sealed class IceMediaAttachmentRelayCandidateTests
{
    [Fact]
    public async Task Relay_local_candidate_is_nominated_when_the_direct_path_does_not_work()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 52001);

        IceMediaAttachment? offerer = null;
        var relayChecks = 0;
        var nominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The direct media socket is a black hole: sends throw, as an unreachable socket would, so the
        // higher-priority host pair never answers and is abandoned. The driver treats a send failure and an
        // unanswered check identically, so this drives it past the host pair to the lower-priority relay pair.
        ValueTask DirectSend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
            => throw new SocketException((int)SocketError.NetworkUnreachable);

        // The relay path answers each check (ordinary and USE-CANDIDATE) by echoing its transaction id, so the
        // relay pair validates and is nominated (RFC 8445 §8.1.1). A Binding Success Response header (0x0101)
        // routes the datagram to the consent response matcher via OnStunPacketReceived.
        ValueTask RelaySend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
        {
            Interlocked.Increment(ref relayChecks);
            var response = new byte[20];
            response[0] = 0x01;
            response[1] = 0x01;
            dg.Span.Slice(8, 12).CopyTo(response.AsSpan(8));
            _ = Task.Run(() => offerer!.OnStunPacketReceived(response, dst));
            return ValueTask.CompletedTask;
        }

        var offererParams = new IceMediaParameters(
            remote, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(remote, Priority: 100)],
        };

        await using (offerer = new IceMediaAttachment(
            offererParams, DirectSend, NullLoggerFactory.Instance,
            onPairNominated: ep => nominated.TrySetResult(ep),
            relaySend: RelaySend))
        {
            offerer.Start();

            var picked = await nominated.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(remote, picked);
            Assert.True(relayChecks >= 1, "the relay send path must have been used to check the relay pair");
        }
    }

    [Fact]
    public async Task Without_a_relay_send_path_only_the_direct_pair_is_checked_and_nominated()
    {
        var offererAddr = new IPEndPoint(IPAddress.Loopback, 52011);
        var answererAddr = new IPEndPoint(IPAddress.Loopback, 52012);

        IceMediaAttachment? offerer = null;
        IceMediaAttachment? answerer = null;

        ValueTask OffererSend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
        {
            var copy = dg.ToArray();
            _ = Task.Run(() => answerer!.OnStunPacketReceived(copy, offererAddr));
            return ValueTask.CompletedTask;
        }

        ValueTask AnswererSend(ReadOnlyMemory<byte> dg, IPEndPoint dst, CancellationToken ct)
        {
            var copy = dg.ToArray();
            _ = Task.Run(() => offerer!.OnStunPacketReceived(copy, answererAddr));
            return ValueTask.CompletedTask;
        }

        var offererParams = new IceMediaParameters(
            answererAddr, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword")
        {
            RemoteCandidates = [new IceRemoteCandidate(answererAddr, Priority: 100)],
        };

        var answererParams = new IceMediaParameters(
            offererAddr, IceEnabled: true, IceControlling: false,
            LocalIceUfrag: "answ", LocalIcePwd: "answPassword", RemoteIceUfrag: "offr", RemoteIcePwd: "offrPassword");

        var offererNominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        // relaySend omitted (null): the direct-only path must still nominate exactly as before — the regression
        // guard for the added relay branch.
        await using (answerer = new IceMediaAttachment(answererParams, AnswererSend, NullLoggerFactory.Instance))
        await using (offerer = new IceMediaAttachment(
            offererParams, OffererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => offererNominated.TrySetResult(ep)))
        {
            answerer.Start();
            offerer.Start();

            Assert.Equal(answererAddr, await offererNominated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
