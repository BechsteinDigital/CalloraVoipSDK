using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3262 opt-in rule (M1 hotfix): reliable provisionals only when the INVITE requires
/// 100rel. Answering a merely-supporting caller with Require: 100rel stalled the 200 OK
/// behind PRACK retransmit timeouts — the caller kept ringing after accept.
/// </summary>
public sealed class ReliableProvisionalOptInTests
{
    private static SipRequest Invite(params (string Name, string Value)[] headers) =>
        new(
            "INVITE",
            "sip:agent@test.local",
            headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase),
            body: string.Empty);

    [Fact]
    public void Require_100rel_uses_reliable_provisionals()
    {
        var invite = Invite(("Require", "100rel"));

        Assert.True(SipCallSessionUtilities.ShouldUseReliableProvisional(invite));
    }

    [Fact]
    public void Supported_100rel_alone_does_not_opt_in()
    {
        var invite = Invite(("Supported", "100rel, timer"));

        Assert.False(SipCallSessionUtilities.ShouldUseReliableProvisional(invite));
    }

    [Fact]
    public void Plain_invite_does_not_opt_in()
    {
        var invite = Invite();

        Assert.False(SipCallSessionUtilities.ShouldUseReliableProvisional(invite));
    }

    [Fact]
    public void Require_other_extension_does_not_opt_in()
    {
        var invite = Invite(("Require", "timer"), ("Supported", "100rel"));

        Assert.False(SipCallSessionUtilities.ShouldUseReliableProvisional(invite));
    }
}
