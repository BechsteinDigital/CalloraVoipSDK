using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE role-conflict resolution (RFC 8445 §7.3.1.1): the agent with the larger-or-equal
/// tie-breaker holds the controlling role; the other switches. A kept role against a peer that
/// claims it is defended with a 487 (Role Conflict) rejection.
/// </summary>
public sealed class IceRoleConflictTests
{
    [Fact]
    public void No_conflict_when_peer_signals_the_opposite_role()
    {
        // We control, peer signals controlled → normal, no switch, no rejection.
        var r1 = IceRoleConflict.Resolve(IceRole.Controlling, peerSignaledControlling: false, 10, 20);
        Assert.Equal(new IceRoleConflictResolution(IceRole.Controlling, false), r1);

        // We are controlled, peer signals controlling → normal.
        var r2 = IceRoleConflict.Resolve(IceRole.Controlled, peerSignaledControlling: true, 10, 20);
        Assert.Equal(new IceRoleConflictResolution(IceRole.Controlled, false), r2);
    }

    [Theory]
    // Both claim controlling. Winner (own >= peer) keeps controlling and rejects; loser switches.
    [InlineData(30, 20, true, true)]
    [InlineData(20, 20, true, true)] // ties go to "own wins"
    [InlineData(10, 20, false, false)]
    public void Both_controlling_conflict_is_resolved_by_tie_breaker(
        ulong own, ulong peer, bool expectedControlling, bool expectedReject)
    {
        var result = IceRoleConflict.Resolve(IceRole.Controlling, peerSignaledControlling: true, own, peer);

        Assert.Equal(expectedControlling ? IceRole.Controlling : IceRole.Controlled, result.Role);
        Assert.Equal(expectedReject, result.Reject487);
    }

    [Theory]
    // Both claim controlled. Winner switches to controlling (no reject); loser keeps controlled and rejects.
    [InlineData(30, 20, true, false)]
    [InlineData(20, 20, true, false)]
    [InlineData(10, 20, false, true)]
    public void Both_controlled_conflict_is_resolved_by_tie_breaker(
        ulong own, ulong peer, bool expectedControlling, bool expectedReject)
    {
        var result = IceRoleConflict.Resolve(IceRole.Controlled, peerSignaledControlling: false, own, peer);

        Assert.Equal(expectedControlling ? IceRole.Controlling : IceRole.Controlled, result.Role);
        Assert.Equal(expectedReject, result.Reject487);
    }

    [Fact]
    public void Tie_breaker_is_nonzero_and_varies()
    {
        // Not a strict guarantee, but a 64-bit CSPRNG collision/zero here is astronomically unlikely.
        var a = IceTieBreaker.Generate();
        var b = IceTieBreaker.Generate();
        Assert.NotEqual(0UL, a | b);
        Assert.NotEqual(a, b);
    }
}
