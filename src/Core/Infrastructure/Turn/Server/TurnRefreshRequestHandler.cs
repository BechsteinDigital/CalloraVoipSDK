using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Handles TURN Refresh requests: allocation lifetime extension, RFC 8016 mobility-ticket resolution
/// and migration, and zero-lifetime teardown. Extracted from <see cref="TurnServer"/> so the
/// refresh/mobility policy lives beside the other request handlers (mirrors
/// <see cref="TurnAllocateRequestHandler"/>) instead of inline in the dispatch class.
/// </summary>
internal sealed class TurnRefreshRequestHandler
{
    private readonly TurnServerOptions _options;
    private readonly TurnServerResponseFactory _responseFactory;
    private readonly TurnMobilityService _mobilityService;
    private readonly TurnAllocationRegistry _registry;

    /// <summary>Creates a handler for Refresh requests.</summary>
    public TurnRefreshRequestHandler(
        TurnServerOptions options,
        TurnServerResponseFactory responseFactory,
        TurnMobilityService mobilityService,
        TurnAllocationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(responseFactory);
        ArgumentNullException.ThrowIfNull(mobilityService);
        ArgumentNullException.ThrowIfNull(registry);

        _options = options;
        _responseFactory = responseFactory;
        _mobilityService = mobilityService;
        _registry = registry;
    }

    /// <summary>Processes one Refresh request and returns the response to send.</summary>
    public async Task<StunMessage> HandleAsync(StunMessage request, TurnClientContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mobilityTicket = TurnAttributeMapper.DecodeMobilityTicket(request);

        TurnServerAllocation? allocation;
        if (mobilityTicket is not null)
        {
            if (!_options.EnableMobility)
                return _responseFactory.BuildErrorResponse(request, 405, "Mobility Forbidden", includeAuthAttributes: false);

            if (mobilityTicket.Ticket.Length == 0)
                return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

            if (!_mobilityService.TryResolveAllocationByTicket(
                    mobilityTicket.Ticket,
                    _registry.Table,
                    out var oldClientKey,
                    out var resolved))
                return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

            if (resolved is null)
                return _responseFactory.BuildErrorResponse(request, 437, "Allocation Mismatch", includeAuthAttributes: false);

            if (string.Equals(oldClientKey, context.ClientKey, StringComparison.Ordinal))
                return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

            if (!_mobilityService.TryMigrateAllocationToClient(
                    _registry.Table,
                    oldClientKey,
                    context,
                    out allocation))
                return _responseFactory.BuildErrorResponse(request, 437, "Allocation Mismatch", includeAuthAttributes: false);
        }
        else if (!_registry.TryGetLive(context.ClientKey, out allocation))
        {
            return _responseFactory.BuildErrorResponse(request, 437, "Allocation Mismatch", includeAuthAttributes: false);
        }

        var requestedFamily = TurnAttributeMapper.DecodeRequestedAddressFamily(request)?.Family;
        if (requestedFamily is not null)
        {
            var allocationFamily = allocation!.RelayedEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? TurnAddressFamily.IPv6
                : TurnAddressFamily.IPv4;

            if (requestedFamily != allocationFamily)
                return _responseFactory.BuildErrorResponse(request, 443, "Peer Address Family Mismatch", includeAuthAttributes: false);
        }

        var requestedLifetime = TurnAttributeMapper.DecodeLifetime(request)?.Seconds;
        if (requestedLifetime == 0)
        {
            await _registry.RemoveAsync(allocation!.ClientKey).ConfigureAwait(false);
            return _responseFactory.BuildSuccessResponse(
                request,
                [TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = 0 })]);
        }

        var lifetime = ClampAllocationLifetime(requestedLifetime);
        allocation!.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(lifetime);
        if (mobilityTicket is not null)
            _mobilityService.RemoveTicket(mobilityTicket.Ticket.Span);

        var responseAttributes = new List<StunAttribute>
        {
            TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetime })
        };
        if (mobilityTicket is not null)
        {
            responseAttributes.Add(TurnAttributeMapper.Encode(new TurnMobilityTicketAttribute
            {
                Ticket = _mobilityService.IssueTicket(allocation)
            }));
        }

        return _responseFactory.BuildSuccessResponse(
            request,
            responseAttributes);
    }

    private uint ClampAllocationLifetime(uint? requestedLifetime)
    {
        if (!requestedLifetime.HasValue)
            return _options.DefaultAllocationLifetimeSeconds;

        return Math.Clamp(
            requestedLifetime.Value,
            0,
            _options.MaxAllocationLifetimeSeconds);
    }
}
