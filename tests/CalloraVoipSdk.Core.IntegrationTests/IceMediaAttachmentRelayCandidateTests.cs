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

    // ── K4: proactive TURN permission on the controlled (answerer) agent ────────────────────────────────────

    [Fact]
    public async Task Controlled_agent_proactively_permissions_a_remote_candidate_seen_after_relay_adoption()
    {
        // The answerer (controlled, no nomination driver) adopts its relay candidate, then a remote candidate
        // trickles in. Its IP must be proactively permissioned (RFC 8656 §9) so the offerer's inbound relay
        // check reaches the answerer instead of being dropped by the TURN server.
        var offererIp = IPAddress.Parse("203.0.113.7");
        var permissioned = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task EnsurePermission(IPAddress ip, CancellationToken ct)
        {
            permissioned.TrySetResult(ip);
            return Task.CompletedTask;
        }

        await using var answerer = ControlledAnswerer();
        answerer.AddRelayLocalCandidate(NoopRelaySend, EnsurePermission);

        answerer.AddRemoteCandidate(new IceRemoteCandidate(new IPEndPoint(offererIp, 50000), Priority: 100));

        Assert.Equal(offererIp, await permissioned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Controlled_agent_backfills_permission_for_remote_candidates_seen_before_relay_adoption()
    {
        // The offer's SDP remote candidates (and early trickle) arrive BEFORE the answerer's allocation finishes
        // gathering, so they are seen before the relay is adopted. AddRelayLocalCandidate must back-fill the
        // permission for every already-seen IP — otherwise those offerer paths' inbound relay checks are dropped.
        var offererIpA = IPAddress.Parse("203.0.113.7");
        var offererIpB = IPAddress.Parse("203.0.113.9");
        var permissioned = new HashSet<IPAddress>();
        var gate = new object();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task EnsurePermission(IPAddress ip, CancellationToken ct)
        {
            lock (gate)
            {
                permissioned.Add(ip);
                if (permissioned.Count == 2)
                    both.TrySetResult();
            }
            return Task.CompletedTask;
        }

        await using var answerer = ControlledAnswerer();

        // Seen before the relay is adopted (the offer's candidates + early trickle).
        answerer.AddRemoteCandidate(new IceRemoteCandidate(new IPEndPoint(offererIpA, 50000), Priority: 100));
        answerer.AddRemoteCandidate(new IceRemoteCandidate(new IPEndPoint(offererIpB, 50000), Priority: 90));

        answerer.AddRelayLocalCandidate(NoopRelaySend, EnsurePermission);

        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (gate)
        {
            Assert.Contains(offererIpA, permissioned);
            Assert.Contains(offererIpB, permissioned);
        }
    }

    [Fact]
    public async Task Controlling_agent_does_not_use_the_proactive_permission_installer()
    {
        // A controlling agent installs a permission when its send path relays a check — the proactive installer
        // must NOT be driven from AddRemoteCandidate on the controlling path, or the permission would be issued
        // twice. This guards the driver/no-driver branch split in AddRemoteCandidate.
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.20"), 50000);
        var proactiveInstalls = 0;

        Task EnsurePermission(IPAddress ip, CancellationToken ct)
        {
            Interlocked.Increment(ref proactiveInstalls);
            return Task.CompletedTask;
        }

        var offererParams = new IceMediaParameters(
            remote, IceEnabled: true, IceControlling: true,
            LocalIceUfrag: "offr", LocalIcePwd: "offrPassword", RemoteIceUfrag: "answ", RemoteIcePwd: "answPassword");

        await using var offerer = new IceMediaAttachment(
            offererParams, NoopRelaySend, NullLoggerFactory.Instance);

        // A controlling agent HAS a nomination driver, so AddRelayLocalCandidate adds a driver candidate; the
        // installer is stored but the proactive path is gated on there being no driver.
        offerer.AddRelayLocalCandidate(NoopRelaySend, EnsurePermission);
        offerer.AddRemoteCandidate(new IceRemoteCandidate(remote, Priority: 100));

        await Task.Delay(150); // give any erroneous proactive install a chance to fire
        Assert.Equal(0, Volatile.Read(ref proactiveInstalls));
    }

    // A controlled (answerer) agent: ICE enabled, IceControlling false → no nomination driver. No remote
    // candidates seeded in the parameters (they arrive via SDP/trickle through AddRemoteCandidate).
    private static IceMediaAttachment ControlledAnswerer() =>
        new(
            new IceMediaParameters(
                new IPEndPoint(IPAddress.Loopback, 51000), IceEnabled: true, IceControlling: false,
                LocalIceUfrag: "answ", LocalIcePwd: "answPassword", RemoteIceUfrag: "offr", RemoteIcePwd: "offrPassword"),
            NoopRelaySend, NullLoggerFactory.Instance);

    private static ValueTask NoopRelaySend(ReadOnlyMemory<byte> datagram, IPEndPoint target, CancellationToken ct)
        => ValueTask.CompletedTask;
}
