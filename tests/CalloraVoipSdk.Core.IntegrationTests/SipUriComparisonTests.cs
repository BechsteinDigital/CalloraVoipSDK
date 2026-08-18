using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3261 §19.1.4 URI comparison, pinned against the worked examples the RFC itself lists. Those examples are
/// the specification's own answer key, so a table built from them cannot drift into testing what the
/// implementation happens to do — which matters here, because the rules are the kind that look obvious and are
/// not: a URI omitting a component with a default value does <em>not</em> match one that states it, while an
/// unknown parameter present on only one side is ignored rather than disqualifying.
/// </summary>
public sealed class SipUriComparisonTests
{
    /// <summary>The equivalent pairs listed in RFC 3261 §19.1.4.</summary>
    public static TheoryData<string, string, string> Equivalent => new()
    {
        { "sip:%61lice@atlanta.com;transport=TCP", "sip:alice@AtlanTa.CoM;Transport=tcp",
          "escaped user, host case and parameter-name/value case are all insignificant" },
        { "sip:carol@chicago.com", "sip:carol@chicago.com;newparam=5",
          "an unknown parameter on one side only is ignored" },
        { "sip:carol@chicago.com;newparam=5", "sip:carol@chicago.com;security=on",
          "two different unknown parameters, each on one side only, are both ignored" },
        { "sip:biloxi.com;transport=tcp;method=REGISTER", "sip:biloxi.com;method=REGISTER;transport=tcp",
          "parameter order is not significant" },
        { "sip:alice@atlanta.com?subject=project%20x&priority=urgent",
          "sip:alice@atlanta.com?priority=urgent&subject=project%20x",
          "header order is not significant" },
    };

    /// <summary>The non-equivalent pairs listed in RFC 3261 §19.1.4.</summary>
    public static TheoryData<string, string, string> NotEquivalent => new()
    {
        { "SIP:ALICE@AtLanTa.CoM;Transport=udp", "sip:alice@AtlanTa.CoM;Transport=UDP",
          "the user part is case-sensitive" },
        { "sip:bob@biloxi.com", "sip:bob@biloxi.com:5060",
          "a URI omitting a defaulted component does not match one that states it" },
        { "sip:bob@biloxi.com", "sip:bob@biloxi.com;transport=udp",
          "same rule for transport: omitted is not the same as explicitly default" },
        { "sip:bob@biloxi.com", "sip:bob@biloxi.com:6000;transport=udp",
          "neither port nor transport matches" },
        { "sip:carol@chicago.com", "sip:carol@chicago.com?Subject=next%20meeting",
          "a header present in only one URI is never ignored" },
    };

    [Theory]
    [MemberData(nameof(Equivalent))]
    public void Rfc3261_equivalent_uris_compare_equal(string uriA, string uriB, string why)
    {
        Assert.True(SipUriProtocol.SipUriEqual(uriA, uriB), $"RFC 3261 §19.1.4: '{uriA}' ≡ '{uriB}' — {why}.");
        Assert.True(SipUriProtocol.SipUriEqual(uriB, uriA), $"comparison must be symmetric — {why}.");
    }

    [Theory]
    [MemberData(nameof(NotEquivalent))]
    public void Rfc3261_non_equivalent_uris_compare_unequal(string uriA, string uriB, string why)
    {
        Assert.False(SipUriProtocol.SipUriEqual(uriA, uriB), $"RFC 3261 §19.1.4: '{uriA}' ≠ '{uriB}' — {why}.");
        Assert.False(SipUriProtocol.SipUriEqual(uriB, uriA), $"comparison must be symmetric — {why}.");
    }

    [Fact]
    public void A_uri_equals_itself_and_null_equals_only_null()
    {
        Assert.True(SipUriProtocol.SipUriEqual("sip:alice@atlanta.com", "sip:alice@atlanta.com"));
        Assert.True(SipUriProtocol.SipUriEqual(null, null));
        Assert.False(SipUriProtocol.SipUriEqual(null, "sip:alice@atlanta.com"));
        Assert.False(SipUriProtocol.SipUriEqual("sip:alice@atlanta.com", null));
    }

    [Fact]
    public void Sip_and_sips_are_different_schemes()
    {
        // Not from the RFC's example list, but §19.1 is explicit that the schemes are distinct — and treating
        // them as equal would let a TLS-required identity be matched by a cleartext one.
        Assert.False(SipUriProtocol.SipUriEqual("sip:alice@atlanta.com", "sips:alice@atlanta.com"));
    }
}
