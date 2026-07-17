using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Handles TURN CreatePermission and ChannelBind requests against an existing allocation: installs
/// peer permissions (RFC 5766 §8, IP-keyed) and channel bindings (§11, full-endpoint keyed) under the
/// per-allocation quotas. Extracted from <see cref="TurnServer"/> so the permission/channel install
/// policy lives in one focused collaborator instead of inline in the dispatch class.
/// </summary>
internal sealed class TurnPermissionRequestHandler
{
    private readonly TurnServerOptions _options;
    private readonly TurnServerResponseFactory _responseFactory;
    private readonly TurnAllocationRegistry _registry;

    /// <summary>Creates a handler for CreatePermission/ChannelBind requests.</summary>
    public TurnPermissionRequestHandler(
        TurnServerOptions options,
        TurnServerResponseFactory responseFactory,
        TurnAllocationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(responseFactory);
        ArgumentNullException.ThrowIfNull(registry);

        _options = options;
        _responseFactory = responseFactory;
        _registry = registry;
    }

    /// <summary>Processes one CreatePermission request and returns the response to send.</summary>
    public StunMessage HandleCreatePermission(StunMessage request, TurnClientContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_registry.TryGetLive(context.ClientKey, out var allocation))
            return _responseFactory.BuildErrorResponse(request, 437, "Allocation Mismatch", includeAuthAttributes: false);

        var peer = TurnAttributeMapper.DecodeXorPeerAddress(request)?.EndPoint;
        if (peer is null)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (!TurnMobilityService.IsPeerFamilyMatchingAllocation(allocation!, peer))
            return _responseFactory.BuildErrorResponse(request, 443, "Peer Address Family Mismatch", includeAuthAttributes: false);

        if (!allocation!.TryUpsertPermission(
                peer,
                DateTimeOffset.UtcNow.AddSeconds(_options.PermissionLifetimeSeconds),
                _options.MaxPermissionsPerAllocation))
        {
            return _responseFactory.BuildErrorResponse(request, 486, "Allocation Quota Reached", includeAuthAttributes: false);
        }

        return _responseFactory.BuildSuccessResponse(request, []);
    }

    /// <summary>Processes one ChannelBind request and returns the response to send.</summary>
    public StunMessage HandleChannelBind(StunMessage request, TurnClientContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_registry.TryGetLive(context.ClientKey, out var allocation))
            return _responseFactory.BuildErrorResponse(request, 437, "Allocation Mismatch", includeAuthAttributes: false);

        var peer = TurnAttributeMapper.DecodeXorPeerAddress(request)?.EndPoint;
        var channel = TurnAttributeMapper.DecodeChannelNumber(request)?.ChannelNumber;

        if (peer is null || !channel.HasValue)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (channel.Value < 0x4000 || channel.Value > 0x7FFF)
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (!TurnMobilityService.IsPeerFamilyMatchingAllocation(allocation!, peer))
            return _responseFactory.BuildErrorResponse(request, 443, "Peer Address Family Mismatch", includeAuthAttributes: false);

        if (!allocation!.IsChannelCompatible(channel.Value, peer))
            return _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false);

        if (!allocation!.TryUpsertPermission(
                peer,
                DateTimeOffset.UtcNow.AddSeconds(_options.PermissionLifetimeSeconds),
                _options.MaxPermissionsPerAllocation)
            || !allocation.TryUpsertChannelBinding(
                channel.Value,
                peer,
                DateTimeOffset.UtcNow.AddSeconds(_options.ChannelBindingLifetimeSeconds),
                _options.MaxChannelBindingsPerAllocation))
        {
            return _responseFactory.BuildErrorResponse(request, 486, "Allocation Quota Reached", includeAuthAttributes: false);
        }

        return _responseFactory.BuildSuccessResponse(request, []);
    }
}
