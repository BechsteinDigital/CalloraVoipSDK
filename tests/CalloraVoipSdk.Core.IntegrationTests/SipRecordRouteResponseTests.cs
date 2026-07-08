using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3261 §12.1.1: responses to dialog-establishing requests must echo the request's
/// Record-Route header field values in order, so in-dialog requests (ACK to the 2xx, BYE)
/// traverse the proxy chain rather than being sent straight to our Contact — the latter is
/// dropped behind a restricted NAT when it arrives from an un-primed far-end node.
/// </summary>
public sealed class SipRecordRouteResponseTests
{
    private static SipCallSessionHeaderService CreateService() =>
        new(new AckTestSipCallSessionContext(new CapturingSipTransportRuntime()));

    private static SipRequest InviteWith(IReadOnlyDictionary<string, string> headers) =>
        new("INVITE", "sip:493075435072@83.135.5.138:14178;transport=udp", headers, string.Empty);

    [Fact]
    public void CreateResponseHeadersFromRequest_copiesRecordRoute_preservingOrder()
    {
        var recordRouteRows = string.Join(
            '\n',
            "<sip:217.10.68.150;lr>",
            "<sip:172.20.40.6;lr;did=c46.a44>",
            "<sip:217.10.68.137;lr>");
        var request = InviteWith(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 217.10.68.150;branch=z9hG4bK-test",
            ["From"] = "<sip:bob@sipconnect.sipgate.de>;tag=remote",
            ["To"] = "<sip:alice@sipconnect.sipgate.de>",
            ["Call-ID"] = "call-rr-test",
            ["CSeq"] = "9238 INVITE",
            ["Record-Route"] = recordRouteRows
        });

        var headers = CreateService().CreateResponseHeadersFromRequest(request, "local-tag", includeContentType: false);

        Assert.True(headers.ContainsKey("Record-Route"));
        var rows = SipHeaderValueStorage.SplitRows(headers["Record-Route"]);
        Assert.Equal(
            new[]
            {
                "<sip:217.10.68.150;lr>",
                "<sip:172.20.40.6;lr;did=c46.a44>",
                "<sip:217.10.68.137;lr>"
            },
            rows);
    }

    [Fact]
    public void CreateResponseHeadersFromRequest_withoutRecordRoute_omitsHeader()
    {
        var request = InviteWith(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 217.10.68.150;branch=z9hG4bK-test",
            ["From"] = "<sip:bob@sipconnect.sipgate.de>;tag=remote",
            ["To"] = "<sip:alice@sipconnect.sipgate.de>",
            ["Call-ID"] = "call-rr-test",
            ["CSeq"] = "9238 INVITE"
        });

        var headers = CreateService().CreateResponseHeadersFromRequest(request, "local-tag", includeContentType: false);

        Assert.False(headers.ContainsKey("Record-Route"));
    }

    [Fact]
    public void CreateResponseHeadersFromRequest_singleRecordRoute_isCopied()
    {
        var request = InviteWith(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 217.10.68.150;branch=z9hG4bK-test",
            ["From"] = "<sip:bob@sipconnect.sipgate.de>;tag=remote",
            ["To"] = "<sip:alice@sipconnect.sipgate.de>",
            ["Call-ID"] = "call-rr-test",
            ["CSeq"] = "9238 INVITE",
            ["Record-Route"] = "<sip:217.10.68.150;lr>"
        });

        var headers = CreateService().CreateResponseHeadersFromRequest(request, "local-tag", includeContentType: false);

        var row = Assert.Single(SipHeaderValueStorage.SplitRows(headers["Record-Route"]));
        Assert.Equal("<sip:217.10.68.150;lr>", row);
    }
}
