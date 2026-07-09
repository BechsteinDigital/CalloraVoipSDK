using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The ICE connectivity-check attribute set (RFC 8445 §7.2.2): PRIORITY, the role attribute
/// carrying the tie-breaker, and USE-CANDIDATE only when the controlling agent nominates.
/// </summary>
public sealed class StunIceCheckAttributesTests
{
    [Fact]
    public void Controlling_check_carries_priority_and_ice_controlling()
    {
        var attrs = StunIceCheckAttributes.Build(priority: 1862270975, isControlling: true, tieBreaker: 0xABCDEF12UL, useCandidate: false);

        var priority = Assert.IsType<PriorityAttribute>(Assert.Single(attrs, a => a is PriorityAttribute));
        Assert.Equal(1862270975u, priority.Value);

        var controlling = Assert.IsType<IceControllingAttribute>(Assert.Single(attrs, a => a is IceControllingAttribute));
        Assert.Equal(0xABCDEF12UL, controlling.TieBreaker);

        Assert.DoesNotContain(attrs, a => a is IceControlledAttribute);
        Assert.DoesNotContain(attrs, a => a is UseCandidateAttribute);
    }

    [Fact]
    public void Controlled_check_carries_ice_controlled()
    {
        var attrs = StunIceCheckAttributes.Build(priority: 1000, isControlling: false, tieBreaker: 42UL, useCandidate: false);

        var controlled = Assert.IsType<IceControlledAttribute>(Assert.Single(attrs, a => a is IceControlledAttribute));
        Assert.Equal(42UL, controlled.TieBreaker);
        Assert.DoesNotContain(attrs, a => a is IceControllingAttribute);
    }

    [Fact]
    public void Use_candidate_is_added_only_when_controlling_nominates()
    {
        Assert.Contains(
            StunIceCheckAttributes.Build(1000, isControlling: true, tieBreaker: 1UL, useCandidate: true),
            a => a is UseCandidateAttribute);

        // A controlled agent never nominates, even if asked.
        Assert.DoesNotContain(
            StunIceCheckAttributes.Build(1000, isControlling: false, tieBreaker: 1UL, useCandidate: true),
            a => a is UseCandidateAttribute);
    }
}
