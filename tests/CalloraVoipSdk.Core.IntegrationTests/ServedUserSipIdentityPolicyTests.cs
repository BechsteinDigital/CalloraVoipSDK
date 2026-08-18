using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The served-user policy (RFC 3261 §8.2.2.1): a UAS that does not serve the addressed user answers 404. This is
/// the first real caller of the §19.1.4 URI comparison, and the reason that comparison has to be a comparison
/// rather than <c>string.Equals</c> — the difference decides whether a call is answered or turned away.
/// </summary>
public sealed class ServedUserSipIdentityPolicyTests
{
    // Typed as the contract the compliance matrix names for §8.2.2.1, not as the implementation: what is
    // claimed is that a UAS can be told which users it serves, and that is the interface.
    private static ISipUasUserIdentityPolicy Policy(params string[] aors) => new ServedUserSipIdentityPolicy(aors);

    [Fact]
    public void A_served_user_is_matched_regardless_of_how_the_peer_spells_it()
    {
        var policy = Policy("sip:alice@example.com");

        Assert.True(policy.IsServedUser("sip:alice@example.com"));
        Assert.True(policy.IsServedUser("sip:alice@Example.COM"));   // host is case-insensitive
        Assert.True(policy.IsServedUser("SIP:alice@example.com"));   // scheme is case-insensitive
        Assert.True(policy.IsServedUser("sip:%61lice@example.com")); // unreserved escapes are equivalent
    }

    [Fact]
    public void An_unserved_user_is_rejected()
    {
        var policy = Policy("sip:alice@example.com");

        Assert.False(policy.IsServedUser("sip:bob@example.com"));
        Assert.False(policy.IsServedUser("sip:Alice@example.com"));  // the user part IS case-sensitive
        Assert.False(policy.IsServedUser("sip:alice@other.com"));
        Assert.False(policy.IsServedUser("sips:alice@example.com")); // a TLS identity is a different address
    }

    [Fact]
    public void A_stated_port_is_a_different_address_than_an_omitted_one()
    {
        // The rule most likely to surprise, so it is pinned: RFC 3261 §19.1.4 lists exactly this pair as NOT
        // equivalent, because the URI that omits the port stays free to resolve elsewhere. An operator who
        // configures the bare form and whose peers send the explicit form gets 404s — which is why the
        // configuration doc says to list the form the peers actually send.
        Assert.False(Policy("sip:alice@example.com").IsServedUser("sip:alice@example.com:5060"));
        Assert.False(Policy("sip:alice@example.com:5060").IsServedUser("sip:alice@example.com"));
        Assert.True(Policy("sip:alice@example.com:5060").IsServedUser("sip:alice@example.com:5060"));
    }

    [Fact]
    public void Any_one_of_several_served_addresses_matches()
    {
        var policy = Policy("sip:alice@example.com", "sip:support@example.com", "sip:+4930111@example.com");

        Assert.True(policy.IsServedUser("sip:support@EXAMPLE.com"));
        Assert.True(policy.IsServedUser("sip:+4930111@example.com"));
        Assert.False(policy.IsServedUser("sip:sales@example.com"));
    }

    [Fact]
    public void An_absent_or_blank_request_uri_is_not_served()
    {
        var policy = Policy("sip:alice@example.com");

        Assert.False(policy.IsServedUser(""));
        Assert.False(policy.IsServedUser("   "));
    }

    [Fact]
    public void An_empty_served_set_is_refused_rather_than_silently_blocking_everything()
    {
        // "No addresses configured" must never be read as "serve nobody" — that would turn an unset option into
        // an outage. The composition root uses the accept-all policy instead, so this constructor refuses.
        Assert.Throws<ArgumentException>(() => new ServedUserSipIdentityPolicy([]));
    }
}
