using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Routes one outbound in-dialog request per RFC 3261 §12.2.1.1, keeping two concerns separate:
/// <list type="bullet">
/// <item><description>
/// the <em>message composition</em> — the Request-URI and Route header set, including the strict-router rewrite
/// (Request-URI := the topmost route, route set := the remaining routes + the remote target) when the topmost
/// route is a strict router — computed by <see cref="Plan"/> from the dialog remote target and route set; and
/// </description></item>
/// <item><description>
/// the <em>transport destination</em> — a routed dialog (non-empty route set) sends to the resolved topmost
/// route; a direct dialog (empty route set) sends to the learned response source (the received=/rport address).
/// §12.2 governs the Request-URI, not necessarily the physical send target, so using the observed source
/// address for a direct dialog is "other information" rather than a violation — and it keeps in-dialog requests
/// reaching a UA behind NAT whose Contact is a private address.
/// </description></item>
/// </list>
/// This corrects the earlier behaviour that sent every in-dialog request straight to the last response's source
/// socket and never derived the next hop (nor the strict-router rewrite) from the dialog route set.
/// </summary>
internal static class SipInDialogRequestRouting
{
    /// <summary>
    /// Computes the RFC 3261 §12.2.1.1 Request-URI, Route header set, and topmost-route next-hop URI from the
    /// dialog's remote target and route set. An empty route set yields the remote target as both Request-URI and
    /// next hop with no Route header; a loose topmost route keeps the remote target as Request-URI and the full
    /// route set; a strict topmost route rewrites the Request-URI to that route and moves the remote target to
    /// the end of the route set.
    /// </summary>
    /// <param name="remoteTargetUri">The dialog remote target URI (the peer Contact).</param>
    /// <param name="routeSet">The dialog route set (reversed Record-Route), in order.</param>
    public static SipInDialogRoutingPlan Plan(string remoteTargetUri, IReadOnlyList<string> routeSet)
    {
        if (routeSet is null || routeSet.Count == 0)
            return new SipInDialogRoutingPlan(remoteTargetUri, [], remoteTargetUri, ResolveNextHop: false);

        try
        {
            // §12.2.1.1 is the same loose/strict/empty algorithm the initial-request planner runs, so reuse it
            // as the one source of truth. It validates URIs and throws on malformed input, which an in-dialog
            // request must survive rather than abort on.
            var target = SipInitialRequestRoutingPlanner.CreateInitialTarget(remoteTargetUri, routeSet);
            return new SipInDialogRoutingPlan(target.RequestUri, target.RouteSet, target.NextHopUri, ResolveNextHop: true);
        }
        catch (ArgumentException)
        {
            // A malformed remote target or route-set entry must not abort a BYE/re-INVITE: keep the raw route
            // header best-effort, but report ResolveNextHop:false so the caller sends to the learned response
            // source rather than resolving a possibly-bogus (e.g. private/NAT) target address.
            return new SipInDialogRoutingPlan(remoteTargetUri, routeSet, remoteTargetUri, ResolveNextHop: false);
        }
    }

    /// <summary>
    /// Applies §12.2.1.1 routing to an outbound in-dialog request: overrides the <c>Route</c> header with the
    /// composed route set (or removes it for a direct dialog) and returns the effective Request-URI and the
    /// resolved transport destination. A routed dialog resolves the topmost route to an endpoint; a direct
    /// dialog — or a topmost route that fails to resolve — uses the learned response source
    /// (<see cref="ISipCallSessionContext.RemoteEndPoint"/>).
    /// </summary>
    /// <param name="context">The dialog session context (remote target, route set, transport, response source).</param>
    /// <param name="headers">The request headers whose <c>Route</c> header is rewritten in place.</param>
    /// <param name="ct">Cancellation token for the next-hop resolution.</param>
    public static async Task<(string RequestUri, IPEndPoint RemoteEndPoint)> ApplyInDialogRoutingAsync(
        ISipCallSessionContext context, IDictionary<string, string> headers, CancellationToken ct)
    {
        var plan = Plan(context.RemoteRequestUri, context.RouteSet);

        if (plan.RouteHeaderSet.Count > 0)
            headers["Route"] = string.Join(", ", plan.RouteHeaderSet.Select(uri => $"<{uri}>"));
        else
            headers.Remove("Route");

        var remoteEndPoint = context.RemoteEndPoint;
        if (plan.ResolveNextHop)
        {
            remoteEndPoint = await ResolveNextHopAsync(context, plan.NextHopUri, ct).ConfigureAwait(false)
                             ?? context.RemoteEndPoint;
        }

        return (plan.RequestUri, remoteEndPoint);
    }

    // Resolves the topmost route URI to a concrete transport endpoint via the transport's route resolver
    // (RFC 3263). Returns null — the caller then falls back to the learned response source — when the URI is
    // unparseable, resolves to nothing, or resolution throws; a resolution hiccup must never abort the request.
    private static async Task<IPEndPoint?> ResolveNextHopAsync(
        ISipCallSessionContext context, string nextHopUri, CancellationToken ct)
    {
        if (!SipProtocol.TryParseSipUri(nextHopUri, out _, out var host, out var portFromUri))
            return null;

        var secure = SipProtocol.IsSipsUri(nextHopUri);
        var port = portFromUri ?? (secure ? 5061 : 5060);
        try
        {
            var candidates = await context.Transport
                .ResolveRemoteRouteCandidatesAsync(host, port, context.SignalingTransport, ct)
                .ConfigureAwait(false);
            return candidates.Count > 0 ? candidates[0].EndPoint : null;
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(
                ex, "In-dialog next-hop resolution for {NextHop} failed; using the learned response source.", nextHopUri);
            return null;
        }
    }
}

/// <summary>
/// The RFC 3261 §12.2.1.1 routing of one in-dialog request: the Request-URI, the Route header set (in order),
/// the topmost-route next-hop URI, and whether the dialog has a route set at all.
/// </summary>
/// <param name="RequestUri">The effective Request-URI (remote target, or the strict topmost route).</param>
/// <param name="RouteHeaderSet">The composed Route header set (empty for a direct dialog).</param>
/// <param name="NextHopUri">The topmost-route URI to resolve as the transport next hop (remote target when empty).</param>
/// <param name="ResolveNextHop">
/// Whether the transport next hop should be resolved from <see cref="NextHopUri"/>: <see langword="true"/> for a
/// routed dialog with a well-formed route set; <see langword="false"/> for a direct dialog or a route set that
/// could not be planned, both of which send to the learned response source instead.
/// </param>
internal readonly record struct SipInDialogRoutingPlan(
    string RequestUri,
    IReadOnlyList<string> RouteHeaderSet,
    string NextHopUri,
    bool ResolveNextHop);
