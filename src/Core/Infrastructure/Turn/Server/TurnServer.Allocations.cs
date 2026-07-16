using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Allocation-table lifecycle for <see cref="TurnServer"/>: registration, removal, relay task
/// wiring, quota capacity checks and the background expiry sweep. Split from the transport/dispatch
/// half of the class to keep each file focused (and under the source-length rule).
/// </summary>
internal sealed partial class TurnServer
{
    private async Task ReplaceAllocationAsync(TurnServerAllocation allocation, CancellationToken ct)
    {
        TurnServerAllocation? previousAllocation;
        await _allocationMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _allocationsByClient.TryRemove(allocation.ClientKey, out previousAllocation);
            _relayTasks.TryRemove(allocation.ClientKey, out _);

            _allocationsByClient[allocation.ClientKey] = allocation;
            var relayTask = StartRelayTask(allocation, ct);
            if (relayTask != Task.CompletedTask)
                _relayTasks[allocation.ClientKey] = relayTask;
        }
        finally
        {
            _allocationMutationGate.Release();
        }

        if (previousAllocation is not null)
            await DisposeAllocationResourcesAsync(previousAllocation).ConfigureAwait(false);
    }

    private async Task RemoveAllocationAsync(string clientKey)
    {
        TurnServerAllocation? allocation;
        await _allocationMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _allocationsByClient.TryRemove(clientKey, out allocation);
            _relayTasks.TryRemove(clientKey, out _);
        }
        finally
        {
            _allocationMutationGate.Release();
        }

        if (allocation is not null)
            await DisposeAllocationResourcesAsync(allocation).ConfigureAwait(false);
    }

    private Task StartRelayTask(TurnServerAllocation allocation, CancellationToken ct)
    {
        return allocation.RelayedTransport switch
        {
            TurnRequestedTransportProtocol.Udp => RelayReceiveLoopAsync(allocation, ct),
            TurnRequestedTransportProtocol.Tcp => _tcpPassiveConnectionService.RunAsync(allocation, SendToClientAsync, ct),
            _ => Task.CompletedTask
        };
    }

    private async Task DisposeAllocationResourcesAsync(TurnServerAllocation allocation)
    {
        _mobilityService.RemoveTicketsForAllocation(allocation.ClientKey);
        _tcpConnectionBroker.RemoveByAllocation(allocation.ClientKey);
        await allocation.DisposeAsync().ConfigureAwait(false);
    }

    private bool TryGetLiveAllocation(string clientKey, out TurnServerAllocation? allocation)
    {
        allocation = null;

        if (!_allocationsByClient.TryGetValue(clientKey, out var existing))
            return false;

        if (DateTimeOffset.UtcNow <= existing.ExpiresAtUtc)
        {
            allocation = existing;
            return true;
        }

        _ = RemoveAllocationAsync(clientKey);
        return false;
    }

    private bool HasAllocationCapacity(string clientKey)
        => _options.MaxTotalAllocations <= 0
           || _allocationsByClient.ContainsKey(clientKey)
           || _allocationsByClient.Count < _options.MaxTotalAllocations;

    private async Task SweepExpiredAllocationsAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1u, _options.AllocationSweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var entry in _allocationsByClient.ToArray())
                {
                    if (now > entry.Value.ExpiresAtUtc)
                    {
                        await RemoveAllocationAsync(entry.Value.ClientKey).ConfigureAwait(false);
                        continue;
                    }

                    entry.Value.PruneExpired(now);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("TURN allocation sweep loop cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TURN allocation sweep loop failed");
        }
    }

    private static bool IsPeerFamilyMatchingAllocation(TurnServerAllocation allocation, IPEndPoint peerEndPoint)
        => TurnMobilityService.IsPeerFamilyMatchingAllocation(allocation, peerEndPoint);

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
