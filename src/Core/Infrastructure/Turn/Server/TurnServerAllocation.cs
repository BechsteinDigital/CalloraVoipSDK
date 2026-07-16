using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Mutable server-side allocation state for one TURN client.
/// </summary>
internal sealed class TurnServerAllocation : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TurnServerPermission> _permissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ushort, TurnServerChannelBinding> _channelsByNumber = new();
    private readonly ConcurrentDictionary<string, TurnServerChannelBinding> _channelsByPeer = new(StringComparer.Ordinal);

    // Serializes the compound "count-check then insert" so a per-allocation quota is enforced
    // exactly. Reads (HasValidPermission, TryResolve*) stay lock-free on the concurrent maps.
    private readonly object _quotaGate = new();

    /// <summary>
    /// Logical client key used in allocation lookup tables.
    /// </summary>
    public required string ClientKey { get; set; }

    /// <summary>
    /// Client transport that owns the allocation.
    /// </summary>
    public required TurnServerTransport ClientTransport { get; init; }

    /// <summary>
    /// UDP client endpoint when the allocation belongs to a UDP client.
    /// </summary>
    public IPEndPoint? ClientUdpEndPoint { get; set; }

    /// <summary>
    /// Stream connection when the allocation belongs to a TCP/TLS client.
    /// </summary>
    public TurnStreamConnection? ClientStreamConnection { get; init; }

    /// <summary>
    /// Requested relayed transport for this allocation.
    /// </summary>
    public required TurnRequestedTransportProtocol RelayedTransport { get; init; }

    /// <summary>
    /// UDP relay socket for UDP allocations.
    /// </summary>
    public UdpClient? RelaySocket { get; init; }

    /// <summary>
    /// Cancellation source for UDP relay receive loop.
    /// </summary>
    public CancellationTokenSource? RelayStop { get; init; }

    /// <summary>
    /// TCP listener for TCP allocations.
    /// </summary>
    public TcpListener? RelayTcpListener { get; init; }

    /// <summary>
    /// Cancellation source for TCP relay accept loop.
    /// </summary>
    public CancellationTokenSource? RelayTcpStop { get; init; }

    /// <summary>
    /// Relayed endpoint exposed to peers.
    /// </summary>
    public required IPEndPoint RelayedEndPoint { get; init; }

    /// <summary>
    /// Mapped endpoint of the client as observed by the TURN server.
    /// </summary>
    public required IPEndPoint MappedEndPoint { get; init; }

    /// <summary>
    /// Allocation expiry in UTC.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>
    /// Last authenticated username associated with this allocation.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Last authenticated realm associated with this allocation.
    /// </summary>
    public string? Realm { get; set; }

    /// <summary>
    /// Adds or updates permission for the peer endpoint, honouring the per-allocation quota.
    /// Returns false when a new peer would exceed <paramref name="maxPermissions"/> (0 = unlimited).
    /// Refreshing an existing permission always succeeds.
    /// </summary>
    public bool TryUpsertPermission(IPEndPoint peerEndPoint, DateTimeOffset expiresAtUtc, int maxPermissions)
    {
        var key = ToEndpointKey(peerEndPoint);
        lock (_quotaGate)
        {
            if (maxPermissions > 0
                && !_permissions.ContainsKey(key)
                && _permissions.Count >= maxPermissions)
            {
                return false;
            }

            _permissions[key] = new TurnServerPermission
            {
                PeerEndPoint = peerEndPoint,
                ExpiresAtUtc = expiresAtUtc
            };
            return true;
        }
    }

    /// <summary>
    /// Returns true when the peer endpoint is currently permitted.
    /// </summary>
    public bool HasValidPermission(IPEndPoint peerEndPoint, DateTimeOffset nowUtc)
    {
        var key = ToEndpointKey(peerEndPoint);
        if (!_permissions.TryGetValue(key, out var permission))
            return false;

        if (nowUtc <= permission.ExpiresAtUtc)
            return true;

        _permissions.TryRemove(key, out _);
        return false;
    }

    /// <summary>
    /// Adds or updates channel binding for the peer endpoint, honouring the per-allocation quota.
    /// Returns false when a new channel would exceed <paramref name="maxChannelBindings"/>
    /// (0 = unlimited). Re-binding an existing channel number always succeeds.
    /// </summary>
    public bool TryUpsertChannelBinding(
        ushort channelNumber,
        IPEndPoint peerEndPoint,
        DateTimeOffset expiresAtUtc,
        int maxChannelBindings)
    {
        lock (_quotaGate)
        {
            if (maxChannelBindings > 0
                && !_channelsByNumber.ContainsKey(channelNumber)
                && _channelsByNumber.Count >= maxChannelBindings)
            {
                return false;
            }

            var binding = new TurnServerChannelBinding
            {
                ChannelNumber = channelNumber,
                PeerEndPoint = peerEndPoint,
                ExpiresAtUtc = expiresAtUtc
            };

            _channelsByNumber[channelNumber] = binding;
            _channelsByPeer[ToEndpointKey(peerEndPoint)] = binding;
            return true;
        }
    }

    /// <summary>
    /// Removes permissions and channel bindings whose lifetime has elapsed. Invoked by the server's
    /// background sweep so stale entries are reclaimed even without further client traffic.
    /// </summary>
    public void PruneExpired(DateTimeOffset nowUtc)
    {
        foreach (var entry in _permissions)
        {
            if (nowUtc > entry.Value.ExpiresAtUtc)
                _permissions.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _channelsByNumber)
        {
            if (nowUtc > entry.Value.ExpiresAtUtc)
                RemoveChannelBinding(entry.Value);
        }
    }

    /// <summary>
    /// Resolves peer endpoint by channel number when binding is valid.
    /// </summary>
    public bool TryResolvePeerByChannel(ushort channelNumber, DateTimeOffset nowUtc, out IPEndPoint? peerEndPoint)
    {
        peerEndPoint = null;
        if (!_channelsByNumber.TryGetValue(channelNumber, out var binding))
            return false;

        if (nowUtc <= binding.ExpiresAtUtc)
        {
            peerEndPoint = binding.PeerEndPoint;
            return true;
        }

        RemoveChannelBinding(binding);
        return false;
    }

    /// <summary>
    /// Resolves channel number by peer endpoint when binding is valid.
    /// </summary>
    public bool TryResolveChannelByPeer(IPEndPoint peerEndPoint, DateTimeOffset nowUtc, out ushort channelNumber)
    {
        channelNumber = 0;
        var key = ToEndpointKey(peerEndPoint);
        if (!_channelsByPeer.TryGetValue(key, out var binding))
            return false;

        if (nowUtc <= binding.ExpiresAtUtc)
        {
            channelNumber = binding.ChannelNumber;
            return true;
        }

        RemoveChannelBinding(binding);
        return false;
    }

    /// <summary>
    /// Validates that a channel number can be associated with the given peer.
    /// </summary>
    public bool IsChannelCompatible(ushort channelNumber, IPEndPoint peerEndPoint)
    {
        if (_channelsByNumber.TryGetValue(channelNumber, out var byNumber)
            && !byNumber.PeerEndPoint.Equals(peerEndPoint))
        {
            return false;
        }

        var key = ToEndpointKey(peerEndPoint);
        return !_channelsByPeer.TryGetValue(key, out var byPeer)
               || byPeer.ChannelNumber == channelNumber;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (RelayStop is not null)
        {
            try
            {
                RelayStop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            RelayStop.Dispose();
        }

        if (RelayTcpStop is not null)
        {
            try
            {
                RelayTcpStop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            RelayTcpStop.Dispose();
        }

        RelaySocket?.Dispose();
        RelayTcpListener?.Stop();
        return ValueTask.CompletedTask;
    }

    private void RemoveChannelBinding(TurnServerChannelBinding binding)
    {
        _channelsByNumber.TryRemove(binding.ChannelNumber, out _);
        _channelsByPeer.TryRemove(ToEndpointKey(binding.PeerEndPoint), out _);
    }

    private static string ToEndpointKey(IPEndPoint endPoint) => $"{endPoint.Address}:{endPoint.Port}";
}
