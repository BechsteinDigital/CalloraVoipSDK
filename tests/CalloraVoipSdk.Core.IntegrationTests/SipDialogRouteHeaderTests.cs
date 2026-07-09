using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound in-dialog requests (ACK, BYE) must carry a <c>Route</c> header built from the
/// dialog route set (RFC 3261 §12.2.1.1). Behind NAT this is what keeps the ACK/BYE on the
/// proxy chain — sending them straight to the peer's Contact (a private address) is the
/// failure mode the Record-Route echo fixed on the inbound side; this locks the outbound half.
/// </summary>
public sealed class SipDialogRouteHeaderTests
{
    private static SipCallSessionHeaderService HeaderService(params string[] routeSet) =>
        new(new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            RemoteTag = "remote-tag",
            RouteSet = routeSet,
        });

    [Fact]
    public void Bye_carries_route_headers_from_the_route_set_in_order()
    {
        var service = HeaderService("sip:proxy1.example.net;lr", "sip:proxy2.example.net;lr");

        var headers = service.CreateDialogRequestHeaders(
            method: "BYE",
            cseq: 2,
            branch: "z9hG4bK-bye",
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);

        Assert.Equal("<sip:proxy1.example.net;lr>, <sip:proxy2.example.net;lr>", headers["Route"]);
    }

    [Fact]
    public void Ack_carries_the_route_header_too()
    {
        var service = HeaderService("sip:edge.example.net;lr");

        var headers = service.CreateDialogRequestHeaders(
            method: "ACK",
            cseq: 1,
            branch: "z9hG4bK-ack",
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);

        Assert.Equal("<sip:edge.example.net;lr>", headers["Route"]);
    }

    [Fact]
    public void Empty_route_set_adds_no_route_header()
    {
        var service = HeaderService();

        var headers = service.CreateDialogRequestHeaders(
            method: "BYE",
            cseq: 2,
            branch: "z9hG4bK-bye",
            authorizationHeaderName: null,
            authorizationHeader: null,
            includeContentType: false);

        Assert.False(headers.ContainsKey("Route"));
    }
}
