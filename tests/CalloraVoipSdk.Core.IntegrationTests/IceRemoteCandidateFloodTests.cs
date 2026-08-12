using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Negative-test evidence for the trickle-candidate flood cap (#189, from #155 P1-1). A peer may trickle
/// unlimited unique remote-candidate IPs (RFC 8838); each one otherwise grows a persistent per-attachment
/// set and — on a controlled agent with a relay — drives a proactive <c>CreatePermission</c> against the
/// TURN server (RFC 8656 §9). <see cref="IceMediaAttachment"/> caps the distinct IPs it will admit at 256.
/// This pins the ceiling so it cannot silently regress: above it there is no permission transaction, and an
/// already-admitted IP keeps working.
/// </summary>
public sealed class IceRemoteCandidateFloodTests
{
    private const int SeenRemoteAddressCap = 256;
    private static readonly IPEndPoint NominatedRemote = new(IPAddress.Loopback, 42000);

    // The controlled (answerer) role is the one that installs permissions proactively for trickled
    // candidates — a controlling agent installs them from its own relayed check instead.
    private static IceMediaParameters ControlledParameters() => new(
        NominatedRemote, IceEnabled: true, IceControlling: false,
        LocalIceUfrag: "answ", LocalIcePwd: "answPassword",
        RemoteIceUfrag: "offr", RemoteIcePwd: "offrPassword");

    // Distinct IPs from 10.x.y.z, one per index — well past the cap without colliding with the loopback
    // remote or the 203.0.113.0/24 documentation range other TURN tests use.
    private static IPEndPoint CandidateAt(int index) =>
        new(new IPAddress([10, (byte)(index >> 8), (byte)(index & 0xFF), 1]), 50000 + (index & 0x3FF));

    private static async Task WaitForPermissionsAsync(ConcurrentDictionary<IPAddress, byte> permissions, int expected)
    {
        // The proactive install is fire-and-forget by design (a check retransmits, so a failed install is
        // retried rather than tearing anything down), so poll instead of awaiting a handle.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (permissions.Count < expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Trickled_remote_candidate_ips_are_capped_and_the_overflow_installs_no_permission()
    {
        var permissions = new ConcurrentDictionary<IPAddress, byte>();
        Task EnsurePermission(IPAddress peer, CancellationToken ct)
        {
            permissions.TryAdd(peer, 0);
            return Task.CompletedTask;
        }

        await using var attachment = new IceMediaAttachment(
            ControlledParameters(),
            (_, _, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance);

        // Adopting the relay local candidate wires the permission installer; without it the controlled
        // agent has no relay to open, and the cap would not be observable from the outside.
        attachment.AddRelayLocalCandidate((_, _, _) => ValueTask.CompletedTask, EnsurePermission);

        // Trickle well past the cap. Every IP is distinct, so an uncapped implementation would install one
        // permission per candidate and grow its seen-address set without bound.
        const int flood = SeenRemoteAddressCap + 64;
        for (var i = 0; i < flood; i++)
            attachment.AddRemoteCandidate(new IceRemoteCandidate(CandidateAt(i), Priority: 100));

        await WaitForPermissionsAsync(permissions, SeenRemoteAddressCap);

        // Exactly the cap was admitted — the overflow got no CreatePermission transaction at all.
        Assert.Equal(SeenRemoteAddressCap, permissions.Count);
        Assert.DoesNotContain(CandidateAt(SeenRemoteAddressCap).Address, permissions.Keys);
        Assert.DoesNotContain(CandidateAt(flood - 1).Address, permissions.Keys);

        // A stable ceiling, not a sliding window: more overflow does not evict an admitted IP.
        for (var i = 0; i < 32; i++)
            attachment.AddRemoteCandidate(new IceRemoteCandidate(CandidateAt(flood + i), Priority: 100));
        await Task.Delay(100);
        Assert.Equal(SeenRemoteAddressCap, permissions.Count);
        Assert.Contains(CandidateAt(0).Address, permissions.Keys);
    }

    [Fact]
    public async Task An_already_admitted_ip_is_re_admitted_after_the_cap_is_reached()
    {
        var installs = new ConcurrentQueue<IPAddress>();
        var permissions = new ConcurrentDictionary<IPAddress, byte>();
        Task EnsurePermission(IPAddress peer, CancellationToken ct)
        {
            installs.Enqueue(peer);
            permissions.TryAdd(peer, 0);
            return Task.CompletedTask;
        }

        await using var attachment = new IceMediaAttachment(
            ControlledParameters(),
            (_, _, _) => ValueTask.CompletedTask,
            NullLoggerFactory.Instance);
        attachment.AddRelayLocalCandidate((_, _, _) => ValueTask.CompletedTask, EnsurePermission);

        for (var i = 0; i < SeenRemoteAddressCap; i++)
            attachment.AddRemoteCandidate(new IceRemoteCandidate(CandidateAt(i), Priority: 100));
        await WaitForPermissionsAsync(permissions, SeenRemoteAddressCap);
        Assert.Equal(SeenRemoteAddressCap, permissions.Count);

        // A known IP always passes the admission check (ContainsKey short-circuits the count test), so a
        // re-trickled candidate — normal during ICE restarts and re-offers — is not starved by the cap.
        installs.Clear();
        attachment.AddRemoteCandidate(new IceRemoteCandidate(CandidateAt(0), Priority: 200));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (installs.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(installs.TryDequeue(out var reinstalled));
        Assert.Equal(CandidateAt(0).Address, reinstalled);
    }
}
