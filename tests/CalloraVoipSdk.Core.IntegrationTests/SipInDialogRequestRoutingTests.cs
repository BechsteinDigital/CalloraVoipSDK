using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// In-dialog request routing (RFC 3261 §12.2.1.1, CF-014). The Request-URI and Route header set are composed
/// from the dialog remote target and route set — including the strict-router rewrite — while the transport
/// destination is chosen separately: the resolved topmost route for a routed dialog, the learned response
/// source for a direct dialog. This locks the fix that previously sent every in-dialog request straight to the
/// last response's source socket and never derived the next hop nor the strict rewrite from the route set.
/// </summary>
public sealed class SipInDialogRequestRoutingTests
{
    private const string RemoteTarget = "sip:bob@example.test";

    // ── Plan: §12.2.1.1 message composition ──────────────────────────────────────────

    [Fact]
    public void Empty_route_set_targets_the_remote_target_with_no_route_header()
    {
        var plan = SipInDialogRequestRouting.Plan(RemoteTarget, []);

        Assert.Equal(RemoteTarget, plan.RequestUri);
        Assert.Empty(plan.RouteHeaderSet);
        Assert.Equal(RemoteTarget, plan.NextHopUri);
        Assert.False(plan.HasRouteSet);
    }

    [Fact]
    public void Loose_route_set_keeps_the_remote_target_as_request_uri_and_the_full_route_set()
    {
        var plan = SipInDialogRequestRouting.Plan(
            RemoteTarget, ["sip:proxy1.example.net;lr", "sip:proxy2.example.net;lr"]);

        Assert.Equal(RemoteTarget, plan.RequestUri);
        Assert.Equal(["sip:proxy1.example.net;lr", "sip:proxy2.example.net;lr"], plan.RouteHeaderSet);
        Assert.Equal("sip:proxy1.example.net;lr", plan.NextHopUri);
        Assert.True(plan.HasRouteSet);
    }

    [Fact]
    public void Strict_topmost_route_rewrites_the_request_uri_and_appends_the_remote_target()
    {
        // The topmost route has no ;lr → strict router: Request-URI becomes that route, and the remote target
        // moves to the end of the Route set (RFC 3261 §12.2.1.1).
        var plan = SipInDialogRequestRouting.Plan(
            RemoteTarget, ["sip:strict.example.net", "sip:proxy2.example.net;lr"]);

        Assert.Equal("sip:strict.example.net", plan.RequestUri);
        Assert.Equal(["sip:proxy2.example.net;lr", RemoteTarget], plan.RouteHeaderSet);
        Assert.Equal("sip:strict.example.net", plan.NextHopUri);
        Assert.True(plan.HasRouteSet);
    }

    // ── ApplyInDialogRoutingAsync: header override + transport destination ────────────

    [Fact]
    public async Task Direct_dialog_sends_to_the_learned_response_source_and_adds_no_route_header()
    {
        var responseSource = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060);
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            RouteSet = [],
        };
        context.RemoteEndPoint = responseSource;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(context, headers, default);

        Assert.Equal(RemoteTarget, requestUri);
        Assert.False(headers.ContainsKey("Route"));
        Assert.Equal(responseSource, remoteEndPoint); // NAT-safe: the observed source, not the (maybe private) Contact
    }

    [Fact]
    public async Task Routed_dialog_sends_to_the_resolved_topmost_route_not_the_response_source()
    {
        var responseSource = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060);
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            RouteSet = ["sip:proxy1.example.net:6001;lr", "sip:proxy2.example.net:6002;lr"],
        };
        context.RemoteEndPoint = responseSource;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(context, headers, default);

        Assert.Equal(RemoteTarget, requestUri);
        Assert.Equal("<sip:proxy1.example.net:6001;lr>, <sip:proxy2.example.net:6002;lr>", headers["Route"]);
        // The capturing runtime resolves host:port to loopback:port — the topmost route (6001), not the source.
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 6001), remoteEndPoint);
    }

    [Fact]
    public async Task Strict_routed_dialog_rewrites_request_uri_route_header_and_targets_the_strict_hop()
    {
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            RouteSet = ["sip:strict.example.net:6001", "sip:proxy2.example.net:6002;lr"],
        };
        context.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (requestUri, remoteEndPoint) =
            await SipInDialogRequestRouting.ApplyInDialogRoutingAsync(context, headers, default);

        Assert.Equal("sip:strict.example.net:6001", requestUri);
        Assert.Equal($"<sip:proxy2.example.net:6002;lr>, <{RemoteTarget}>", headers["Route"]);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 6001), remoteEndPoint);
    }
}
