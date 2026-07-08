using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// In-dialog Contact NAT fix (media-NAT package, part A): responses to inbound requests must
/// carry the learned/configured public host in the Contact, so a trunk peer can route the
/// ACK to our 2xx. Without it the private LAN Contact caused an endless 200 OK retransmit.
/// </summary>
public sealed class SipInDialogPublicContactTests
{
    private static SipRequest InboundInvite() => new(
        "INVITE",
        "sip:bob@example.test",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bK-abc;rport",
            ["From"] = "<sip:alice@example.test>;tag=remote-tag",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = "call-id-1",
            ["CSeq"] = "1 INVITE",
        },
        body: string.Empty);

    [Fact]
    public void Response_contact_uses_the_advertised_public_host()
    {
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            AdvertisedPublicHost = "83.135.5.138",
            AdvertisedPublicPort = 14257,
        };
        var headerService = new SipCallSessionHeaderService(context);

        var headers = headerService.CreateResponseHeadersFromRequest(
            InboundInvite(), "local-tag", includeContentType: false);

        Assert.Contains("83.135.5.138:14257", headers["Contact"]);
        Assert.DoesNotContain("127.0.0.1", headers["Contact"]);
    }

    [Fact]
    public void Response_contact_falls_back_to_local_without_advertised_host()
    {
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime());
        var headerService = new SipCallSessionHeaderService(context);

        var headers = headerService.CreateResponseHeadersFromRequest(
            InboundInvite(), "local-tag", includeContentType: false);

        // No override → the route-resolved local address (machine-dependent), never the
        // public host; the port stays the local signaling port.
        Assert.DoesNotContain("83.135.5.138", headers["Contact"]);
        Assert.DoesNotContain(":14257", headers["Contact"]);
    }
}
