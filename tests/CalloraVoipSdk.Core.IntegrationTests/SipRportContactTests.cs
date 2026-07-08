using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Package N2: learn the NAT-routable public contact from the registrar's Via
/// received=/rport= and adopt it via idempotent, self-terminating state changes.
/// </summary>
public sealed class SipRportContactTests
{
    // --- Via received=/rport= parsing ---

    [Fact]
    public void Parses_received_and_rport_from_reflected_via()
    {
        var via = "SIP/2.0/UDP 192.168.178.76:45000;branch=z9hG4bK-abc;rport=6543;received=83.135.5.138";

        var (host, port) = SipProtocol.ExtractViaReceivedRport(via);

        Assert.Equal("83.135.5.138", host);
        Assert.Equal(6543, port);
    }

    [Fact]
    public void Returns_nulls_without_received_or_rport()
    {
        var via = "SIP/2.0/UDP 192.168.178.76:45000;branch=z9hG4bK-abc;rport";

        var (host, port) = SipProtocol.ExtractViaReceivedRport(via);

        Assert.Null(host);
        Assert.Null(port);
    }

    [Fact]
    public void Reads_only_the_top_via_of_a_chain()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;received=1.2.3.4;rport=1111, SIP/2.0/UDP 10.0.0.2;received=9.9.9.9;rport=2222";

        var (host, port) = SipProtocol.ExtractViaReceivedRport(via);

        Assert.Equal("1.2.3.4", host);
        Assert.Equal(1111, port);
    }

    // --- Idempotent learn/decision function (loop protection at the pure level) ---

    [Fact]
    public void First_observation_changes_state_and_triggers_correction()
    {
        var (host, port, changed) = NatPublicContactState.ApplyObserved(
            hasManualOverride: false,
            currentHost: null, currentPort: null,
            observedHost: "83.135.5.138", observedPort: 6543);

        Assert.True(changed);
        Assert.Equal("83.135.5.138", host);
        Assert.Equal(6543, port);
    }

    [Fact]
    public void Repeated_identical_observation_does_not_change_state()
    {
        // This is the loop guard: after the corrective register, the next 200 OK reflects
        // the same address, so no further re-registration is triggered.
        var (host, port, changed) = NatPublicContactState.ApplyObserved(
            hasManualOverride: false,
            currentHost: "83.135.5.138", currentPort: 6543,
            observedHost: "83.135.5.138", observedPort: 6543);

        Assert.False(changed);
        Assert.Equal("83.135.5.138", host);
        Assert.Equal(6543, port);
    }

    [Fact]
    public void Changed_public_address_re_learns_once_self_healing()
    {
        var (host, port, changed) = NatPublicContactState.ApplyObserved(
            hasManualOverride: false,
            currentHost: "83.135.5.138", currentPort: 6543,
            observedHost: "83.135.9.99", observedPort: 7000);

        Assert.True(changed);
        Assert.Equal("83.135.9.99", host);
        Assert.Equal(7000, port);
    }

    [Fact]
    public void Manual_override_disables_auto_learning()
    {
        var (host, port, changed) = NatPublicContactState.ApplyObserved(
            hasManualOverride: true,
            currentHost: null, currentPort: null,
            observedHost: "83.135.5.138", observedPort: 6543);

        Assert.False(changed);
        Assert.Null(host);
        Assert.Null(port);
    }

    [Fact]
    public void No_reflection_keeps_current_state()
    {
        var (host, port, changed) = NatPublicContactState.ApplyObserved(
            hasManualOverride: false,
            currentHost: null, currentPort: null,
            observedHost: null, observedPort: null);

        Assert.False(changed);
        Assert.Null(host);
        Assert.Null(port);
    }
}
