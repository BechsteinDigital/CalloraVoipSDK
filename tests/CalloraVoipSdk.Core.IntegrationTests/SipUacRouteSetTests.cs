using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A UAC builds its dialog route set from the <c>Record-Route</c> header of the response,
/// taken in <em>reverse</em> order (RFC 3261 §12.1.2). This is the source of the outbound
/// <c>Route</c> header on in-dialog ACK/BYE (see <see cref="SipDialogRouteHeaderTests"/>), so
/// a wrong order here sends those requests down the wrong proxy path behind NAT.
/// </summary>
public sealed class SipUacRouteSetTests
{
    [Fact]
    public void Route_set_is_the_record_route_reversed()
    {
        var routeSet = SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute(
            "<sip:proxy1.example.net;lr>, <sip:proxy2.example.net;lr>, <sip:proxy3.example.net;lr>");

        Assert.Equal(
            new[]
            {
                "sip:proxy3.example.net;lr",
                "sip:proxy2.example.net;lr",
                "sip:proxy1.example.net;lr",
            },
            routeSet);
    }

    [Fact]
    public void Single_record_route_entry_strips_brackets_and_keeps_uri_parameters()
    {
        var routeSet = SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute(
            "<sip:edge.example.net;lr>");

        Assert.Equal(new[] { "sip:edge.example.net;lr" }, routeSet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_record_route_yields_an_empty_route_set(string? recordRoute)
    {
        Assert.Empty(SipCallSessionTransactionUtilities.ParseRouteSetFromRecordRoute(recordRoute));
    }
}
