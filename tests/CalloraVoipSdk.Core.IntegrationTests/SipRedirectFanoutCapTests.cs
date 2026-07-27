using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [SIP] #13 (design gap): the outbound-INVITE redirect fan-out is bounded, so a 3xx response carrying many
/// Contact URIs cannot spawn an unbounded chain of INVITE transactions (RFC 3261 §8.1.3.4 hardening).
/// </summary>
public sealed class SipRedirectFanoutCapTests
{
    private static SipResponse Redirect(params string[] contacts) => new(
        302,
        "Moved Temporarily",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contact"] = string.Join(", ", contacts.Select(c => $"<{c}>")),
        },
        body: string.Empty);

    [Fact]
    public void Redirect_targets_are_capped_at_the_maximum()
    {
        var response = Redirect("sip:a@h", "sip:b@h", "sip:c@h", "sip:d@h", "sip:e@h");
        var pending = new Queue<SipOutboundInviteTarget>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sip:initial@h" };

        SipOutboundInviteRetryPolicy.EnqueueRedirectTargets(response, pending, visited, maxTargets: 3);

        // initial (1) + 2 redirects = 3 distinct targets; the remaining 3 Contacts are dropped.
        Assert.Equal(2, pending.Count);
        Assert.Equal(3, visited.Count);
    }

    [Fact]
    public void All_redirect_targets_are_enqueued_when_under_the_cap()
    {
        var response = Redirect("sip:a@h", "sip:b@h");
        var pending = new Queue<SipOutboundInviteTarget>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sip:initial@h" };

        SipOutboundInviteRetryPolicy.EnqueueRedirectTargets(response, pending, visited, maxTargets: 8);

        Assert.Equal(2, pending.Count);
        Assert.Equal(3, visited.Count);
    }
}
