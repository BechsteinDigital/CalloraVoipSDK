using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A full controlling ⇄ controlled ICE nomination round-trip (RFC 8445 §7.2.2/§8.1.1) at the media-attachment
/// level. Two <see cref="IceMediaAttachment"/> instances are cross-wired so each one's raw-send feeds the
/// other's <c>OnStunPacketReceived</c> — a real STUN exchange (encode, MESSAGE-INTEGRITY, USE-CANDIDATE,
/// success response, transaction matching) over an in-memory wire, deterministic and socket-timing-free.
/// It proves nomination is gated on a genuine connectivity check (not blind priority) and that both agents
/// converge: the controlling agent nominates the answered candidate, the controlled agent adopts the source
/// of the USE-CANDIDATE check.
/// </summary>
public sealed class IceMediaAttachmentNominationTests
{
    [Fact]
    public async Task Controlling_and_controlled_attachments_converge_on_the_checked_pair()
    {
        var offererAddr = new IPEndPoint(IPAddress.Loopback, 50001);
        var answererAddr = new IPEndPoint(IPAddress.Loopback, 50002);

        IceMediaAttachment? offerer = null;
        IceMediaAttachment? answerer = null;

        // Cross-wire: each attachment's raw send delivers to the other's receive hook, tagged with the
        // sender's address. Delivered asynchronously (Task.Run) to mimic a real UDP socket — the receive loop
        // runs on its own thread — rather than synchronous re-entrancy, a test artifact production never has.
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
        var answererNominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (answerer = new IceMediaAttachment(
            answererParams, AnswererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => answererNominated.TrySetResult(ep)))
        await using (offerer = new IceMediaAttachment(
            offererParams, OffererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => offererNominated.TrySetResult(ep)))
        {
            answerer.Start(); // inbound handler answers checks and adopts a USE-CANDIDATE nomination
            offerer.Start();  // controlling: its driver checks the candidate and nominates with USE-CANDIDATE

            var offererPick = await offererNominated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var answererPick = await answererNominated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The offerer nominated the candidate that answered its check; the answerer adopted the source of
            // the offerer's USE-CANDIDATE check. Both converge on the real pair.
            Assert.Equal(answererAddr, offererPick);
            Assert.Equal(offererAddr, answererPick);
        }
    }

    [Fact]
    public async Task A_trickled_remote_candidate_is_checked_and_nominated_end_to_end()
    {
        var offererAddr = new IPEndPoint(IPAddress.Loopback, 50011);
        var answererAddr = new IPEndPoint(IPAddress.Loopback, 50012);

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

        // The controlling agent starts with NO seed candidates — the answerer's candidate only arrives via
        // trickle (RFC 8838) after start.
        var offererParams = new IceMediaParameters(
            answererAddr, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword");

        var answererParams = new IceMediaParameters(
            offererAddr, IceEnabled: true, IceControlling: false,
            LocalIceUfrag: "answ", LocalIcePwd: "answPassword", RemoteIceUfrag: "offr", RemoteIcePwd: "offrPassword");

        var offererNominated = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (answerer = new IceMediaAttachment(answererParams, AnswererSend, NullLoggerFactory.Instance))
        await using (offerer = new IceMediaAttachment(
            offererParams, OffererSend, NullLoggerFactory.Instance,
            onPairNominated: ep => offererNominated.TrySetResult(ep)))
        {
            answerer.Start();
            offerer.Start();
            offerer.AddRemoteCandidate(new IceRemoteCandidate(answererAddr, Priority: 100)); // trickle after start

            Assert.Equal(answererAddr, await offererNominated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
