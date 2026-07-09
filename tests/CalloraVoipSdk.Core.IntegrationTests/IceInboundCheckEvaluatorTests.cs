using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies inbound ICE check evaluation (RFC 8445 §7.3): USERNAME targeting, role-conflict
/// resolution (§7.3.1.1) and USE-CANDIDATE nomination (§7.3.1.5).
/// </summary>
public sealed class IceInboundCheckEvaluatorTests
{
    private const string LocalUfrag = "ourUfrag";
    private const string OurUsername = "ourUfrag:peerUfrag"; // {our-ufrag}:{peer-ufrag}

    [Fact]
    public void Rejects_when_username_does_not_target_this_agent()
    {
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, "otherUfrag:peerUfrag", IceRole.Controlling, ownTieBreaker: 1,
            peerControlling: false, peerTieBreaker: 0, hasUseCandidate: false);

        Assert.False(result.Accepted);
        Assert.False(result.Reject487);
    }

    [Fact]
    public void Rejects_malformed_username_without_colon()
    {
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, "ourUfrag", IceRole.Controlling, ownTieBreaker: 1,
            peerControlling: false, peerTieBreaker: 0, hasUseCandidate: false);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void Accepts_matching_username_without_role_attribute()
    {
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlling, ownTieBreaker: 1,
            peerControlling: null, peerTieBreaker: 0, hasUseCandidate: false);

        Assert.True(result.Accepted);
        Assert.False(result.Reject487);
        Assert.Equal(IceRole.Controlling, result.RoleAfter);
    }

    [Fact]
    public void No_conflict_when_peer_claims_the_opposite_role()
    {
        // We are controlling; the peer signalled ICE-CONTROLLED — no conflict.
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlling, ownTieBreaker: 10,
            peerControlling: false, peerTieBreaker: 999, hasUseCandidate: false);

        Assert.True(result.Accepted);
        Assert.False(result.Reject487);
        Assert.Equal(IceRole.Controlling, result.RoleAfter);
    }

    [Fact]
    public void Role_conflict_won_rejects_with_487_and_keeps_controlling()
    {
        // Both claim controlling; our tie-breaker is larger → we keep controlling and reject.
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlling, ownTieBreaker: 100,
            peerControlling: true, peerTieBreaker: 50, hasUseCandidate: false);

        Assert.False(result.Accepted);
        Assert.True(result.Reject487);
        Assert.Equal(IceRole.Controlling, result.RoleAfter);
    }

    [Fact]
    public void Role_conflict_lost_switches_role_and_accepts()
    {
        // Both claim controlling; our tie-breaker is smaller → we switch to controlled and accept.
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlling, ownTieBreaker: 50,
            peerControlling: true, peerTieBreaker: 100, hasUseCandidate: false);

        Assert.True(result.Accepted);
        Assert.False(result.Reject487);
        Assert.Equal(IceRole.Controlled, result.RoleAfter);
    }

    [Fact]
    public void Controlled_agent_nominates_on_use_candidate()
    {
        // We are controlled, peer is controlling (no conflict); USE-CANDIDATE nominates the pair.
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlled, ownTieBreaker: 1,
            peerControlling: true, peerTieBreaker: 2, hasUseCandidate: true);

        Assert.True(result.Accepted);
        Assert.True(result.NominatePair);
    }

    [Fact]
    public void Controlling_agent_ignores_use_candidate()
    {
        // We are controlling, peer is controlled; USE-CANDIDATE from a controlled peer is ignored.
        var result = IceInboundCheckEvaluator.Evaluate(
            LocalUfrag, OurUsername, IceRole.Controlling, ownTieBreaker: 1,
            peerControlling: false, peerTieBreaker: 0, hasUseCandidate: true);

        Assert.True(result.Accepted);
        Assert.False(result.NominatePair);
    }
}
