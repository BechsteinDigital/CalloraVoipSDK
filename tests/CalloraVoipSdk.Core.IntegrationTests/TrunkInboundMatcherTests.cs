using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Trunk inbound line-matching (package T1): a SIP trunk delivers the dialed number (DID)
/// as the To-user, not the registration username. The matcher must accept trunk inbound
/// (by peer or domain) without rejecting on the user-part, while a 1:1 user account keeps
/// its exact-username behavior.
/// </summary>
public sealed class TrunkInboundMatcherTests
{
    private const string Domain = "sipconnect.sipgate.de";
    private static readonly IPAddress Registrar = IPAddress.Parse("217.10.68.150");
    private static readonly IReadOnlyCollection<IPAddress> Trusted = [Registrar];

    [Fact]
    public void Trunk_did_on_registered_domain_is_accepted()
    {
        // Regression from the sipgate log: To = dialed number, not the trunk credential.
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:00493075435072@sipconnect.sipgate.de",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: null);

        Assert.True(match);
    }

    [Fact]
    public void Inbound_from_the_registrar_peer_is_accepted_even_off_domain()
    {
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:00493075435072@sbc.internal",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: null);

        Assert.True(match);
    }

    [Fact]
    public void Inbound_from_a_foreign_peer_and_foreign_domain_is_rejected()
    {
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:victim@evil.example",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: IPAddress.Parse("198.51.100.66"),
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: null);

        Assert.False(match);
    }

    [Fact]
    public void Exact_username_account_still_matches()
    {
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:admin123@fritz.box",
            accountUsername: "admin123",
            accountDomain: "fritz.box",
            sourceAddress: null,
            trustedRegistrarAddresses: [],
            inboundNumbers: null);

        Assert.True(match);
    }

    [Fact]
    public void Whitelist_accepts_only_listed_numbers_on_the_domain()
    {
        var accepted = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:00493075435072@sipconnect.sipgate.de",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: ["00493075435072"]);

        var rejected = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:00499999999999@sipconnect.sipgate.de",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: ["00493075435072"]);

        Assert.True(accepted);
        Assert.False(rejected);
    }

    [Fact]
    public void Whitelist_rejects_a_number_on_a_foreign_domain()
    {
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "sip:00493075435072@evil.example",
            accountUsername: "3089553t3",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: ["00493075435072"]);

        Assert.False(match);
    }

    [Fact]
    public void Unparseable_uri_is_rejected()
    {
        var match = TrunkInboundMatcher.IsForThisLine(
            localUri: "not-a-uri",
            accountUsername: "u",
            accountDomain: Domain,
            sourceAddress: Registrar,
            trustedRegistrarAddresses: Trusted,
            inboundNumbers: null);

        Assert.False(match);
    }
}
