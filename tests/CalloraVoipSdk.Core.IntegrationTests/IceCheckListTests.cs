using CalloraVoipSdk.Core.Application.Media.Ice;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE check-list construction (RFC 8445 §6.1.2): pair priority (§6.1.2.3), pairing rules
/// (§6.1.2.2), redundant-pair pruning (§6.1.2.4), priority ordering, and initial
/// Frozen/Waiting state assignment via foundation freezing (§6.1.2.6).
/// </summary>
public sealed class IceCheckListTests
{
    private const long HostPriority = 2130706431; // typ 126 host, RFC 8445 §5.1.2.1

    private static CallIceCandidate Candidate(
        string foundation,
        string type,
        string address,
        int port,
        long priority,
        int component = 1,
        string transport = "UDP",
        string? relatedAddress = null,
        int? relatedPort = null) => new()
    {
        Foundation = foundation,
        Component = component,
        Transport = transport,
        Priority = priority,
        Address = address,
        Port = port,
        Type = type,
        RelatedAddress = relatedAddress,
        RelatedPort = relatedPort,
    };

    [Fact]
    public void Pair_priority_uses_the_rfc8445_formula_for_the_controlling_role()
    {
        // G = controlling (local) = HostPriority, D = controlled (remote) = 1000, G > D.
        // 2^32 * min + 2 * max + 1 = 2^32*1000 + 2*2130706431 + 1.
        Assert.Equal(
            4299228708863L,
            IceCandidatePair.ComputePriority(IceRole.Controlling, HostPriority, 1000));
    }

    [Fact]
    public void Role_swaps_the_g_and_d_terms()
    {
        // Controlled: G = remote = 1000, D = local = HostPriority, G <= D → +0.
        Assert.Equal(
            4299228708862L,
            IceCandidatePair.ComputePriority(IceRole.Controlled, HostPriority, 1000));
    }

    [Fact]
    public void Pairs_form_only_for_a_matching_address_family()
    {
        var locals = new[]
        {
            Candidate("1", "host", "10.0.0.1", 5000, HostPriority),
            Candidate("2", "host", "2001:db8::1", 5000, HostPriority),
        };
        var remotes = new[] { Candidate("9", "host", "9.9.9.9", 7000, 1000) };

        var list = IceCheckList.Create(locals, remotes, IceRole.Controlling);

        // Only the IPv4↔IPv4 pair survives; the IPv6 local cannot pair with an IPv4 remote.
        var pair = Assert.Single(list);
        Assert.Equal("10.0.0.1", pair.Local.Address);
    }

    [Fact]
    public void Check_list_is_ordered_by_priority_descending()
    {
        var locals = new[]
        {
            Candidate("1", "host", "10.0.0.1", 5000, HostPriority),
            Candidate("2", "srflx", "1.2.3.4", 6000, 1694498815, relatedAddress: "10.0.0.2", relatedPort: 5001),
        };
        var remotes = new[] { Candidate("9", "host", "9.9.9.9", 7000, 1000) };

        var list = IceCheckList.Create(locals, remotes, IceRole.Controlling);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].Priority >= list[1].Priority);
        Assert.Equal("host", list[0].Local.Type); // higher candidate priority ranks first
    }

    [Fact]
    public void Foundation_freezing_puts_one_pair_per_foundation_in_waiting()
    {
        // Two locals sharing foundation "1" → two pairs sharing pair-foundation "1|9".
        var locals = new[]
        {
            Candidate("1", "host", "10.0.0.1", 5000, HostPriority),
            Candidate("1", "host", "10.0.0.2", 5001, HostPriority - 100),
        };
        var remotes = new[] { Candidate("9", "host", "9.9.9.9", 7000, 1000) };

        var list = IceCheckList.Create(locals, remotes, IceRole.Controlling);

        Assert.Equal(2, list.Count);
        Assert.Single(list, p => p.State == IceCandidatePairState.Waiting);
        Assert.Single(list, p => p.State == IceCandidatePairState.Frozen);
        // The Waiting pair is the higher-priority one within the foundation.
        var waiting = list.Single(p => p.State == IceCandidatePairState.Waiting);
        Assert.Equal("10.0.0.1", waiting.Local.Address);
    }

    [Fact]
    public void Server_reflexive_pair_redundant_with_its_host_base_is_pruned()
    {
        // srflx base (raddr/rport) equals the host candidate's address/port → for the same
        // remote the two pairs are redundant; the higher-priority (host) one is kept.
        var locals = new[]
        {
            Candidate("h", "host", "10.0.0.1", 5000, HostPriority),
            Candidate("s", "srflx", "1.2.3.4", 6000, 1694498815, relatedAddress: "10.0.0.1", relatedPort: 5000),
        };
        var remotes = new[] { Candidate("9", "host", "9.9.9.9", 7000, 1000) };

        var list = IceCheckList.Create(locals, remotes, IceRole.Controlling);

        var pair = Assert.Single(list);
        Assert.Equal("host", pair.Local.Type);
    }
}
