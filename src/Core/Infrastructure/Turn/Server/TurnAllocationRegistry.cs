using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Owns the TURN allocation table and its lifecycle for <see cref="TurnServer"/>: registration and
/// replacement, removal, relay-task tracking, the server-wide capacity check and the background expiry
/// sweep. Extracted from the transport/dispatch half of the server so allocation state, its mutation
/// gate and the relay-task bookkeeping live behind one collaborator instead of being shared across a
/// partial. The relay loop itself stays in the server and is injected as <c>startRelayTask</c>, so this
/// type stays free of transport/socket concerns.
/// </summary>
internal sealed class TurnAllocationRegistry : IDisposable
{
    private readonly TurnServerOptions _options;
    private readonly ILogger _logger;
    private readonly TurnMobilityService _mobilityService;
    private readonly TurnTcpConnectionBroker _tcpConnectionBroker;
    private readonly Func<TurnServerAllocation, CancellationToken, Task> _startRelayTask;

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TurnServerAllocation> _table = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _relayTasks = new(StringComparer.Ordinal);

    /// <summary>Creates the registry over the active server options and shared services.</summary>
    /// <param name="startRelayTask">
    /// Starts the relay pump for a freshly registered allocation. Returns
    /// <see cref="Task.CompletedTask"/> when the allocation needs no relay task.
    /// </param>
    public TurnAllocationRegistry(
        TurnServerOptions options,
        ILogger logger,
        TurnMobilityService mobilityService,
        TurnTcpConnectionBroker tcpConnectionBroker,
        Func<TurnServerAllocation, CancellationToken, Task> startRelayTask)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mobilityService);
        ArgumentNullException.ThrowIfNull(tcpConnectionBroker);
        ArgumentNullException.ThrowIfNull(startRelayTask);

        _options = options;
        _logger = logger;
        _mobilityService = mobilityService;
        _tcpConnectionBroker = tcpConnectionBroker;
        _startRelayTask = startRelayTask;
    }

    /// <summary>
    /// The live allocation table, exposed for the mobility and TCP-extension collaborators that
    /// operate on it by reference (ticket resolution/migration and stream-connection lookup).
    /// </summary>
    public ConcurrentDictionary<string, TurnServerAllocation> Table => _table;

    /// <summary>Registers (or replaces) the allocation for its client key and starts its relay task.</summary>
    public async Task ReplaceAsync(TurnServerAllocation allocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        TurnServerAllocation? previousAllocation;
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _table.TryRemove(allocation.ClientKey, out previousAllocation);
            _relayTasks.TryRemove(allocation.ClientKey, out _);

            _table[allocation.ClientKey] = allocation;
            var relayTask = _startRelayTask(allocation, ct);
            if (relayTask != Task.CompletedTask)
                _relayTasks[allocation.ClientKey] = relayTask;
        }
        finally
        {
            _mutationGate.Release();
        }

        if (previousAllocation is not null)
            await DisposeAllocationResourcesAsync(previousAllocation).ConfigureAwait(false);
    }

    /// <summary>Removes the allocation for the client key and releases its relay resources.</summary>
    public async Task RemoveAsync(string clientKey)
    {
        TurnServerAllocation? allocation;
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _table.TryRemove(clientKey, out allocation);
            _relayTasks.TryRemove(clientKey, out _);
        }
        finally
        {
            _mutationGate.Release();
        }

        if (allocation is not null)
            await DisposeAllocationResourcesAsync(allocation).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the live allocation for the client key, or <c>false</c> when none exists or it has
    /// expired (an expired entry is removed in the background).
    /// </summary>
    public bool TryGetLive(string clientKey, out TurnServerAllocation? allocation)
    {
        allocation = null;

        if (!_table.TryGetValue(clientKey, out var existing))
            return false;

        if (DateTimeOffset.UtcNow <= existing.ExpiresAtUtc)
        {
            allocation = existing;
            return true;
        }

        _ = RemoveAsync(clientKey);
        return false;
    }

    /// <summary>
    /// Returns <c>false</c> when a new allocation for the client key would exceed the server-wide
    /// allocation quota (an existing allocation for the same key always has capacity).
    /// </summary>
    public bool HasCapacity(string clientKey)
        => _options.MaxTotalAllocations <= 0
           || _table.ContainsKey(clientKey)
           || _table.Count < _options.MaxTotalAllocations;

    /// <summary>
    /// Runs the background sweep that removes expired allocations and prunes expired permissions and
    /// channel bindings, independent of client traffic, until cancelled.
    /// </summary>
    public async Task RunSweepAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1u, _options.AllocationSweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var entry in _table.ToArray())
                {
                    if (now > entry.Value.ExpiresAtUtc)
                    {
                        await RemoveAsync(entry.Value.ClientKey).ConfigureAwait(false);
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

    /// <summary>Removes every allocation and awaits any still-running relay tasks (shutdown).</summary>
    public async Task DisposeAllAsync()
    {
        foreach (var key in _table.Keys.ToArray())
            await RemoveAsync(key).ConfigureAwait(false);

        // RemoveAsync already drained _relayTasks for each key, so this is a defensive await for any
        // relay task that somehow outlived its entry; normally the snapshot is empty here.
        var relayTasks = _relayTasks.Values.ToArray();
        if (relayTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(relayTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TURN relay tasks completed with errors during shutdown");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _mutationGate.Dispose();

    private async Task DisposeAllocationResourcesAsync(TurnServerAllocation allocation)
    {
        _mobilityService.RemoveTicketsForAllocation(allocation.ClientKey);
        _tcpConnectionBroker.RemoveByAllocation(allocation.ClientKey);
        await allocation.DisposeAsync().ConfigureAwait(false);
    }
}
