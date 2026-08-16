using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Tracks inbound SIP subscription leases, refreshes, explicit termination, and timeout expiry.
/// </summary>
internal sealed class SipSubscriptionLifecycleManager : IDisposable
{
    /// <summary>
    /// Longest subscription lifetime granted, whatever the peer asks for (#158 P2-12).
    /// </summary>
    /// <remarks>
    /// RFC 6665 §4.2.1 explicitly lets the notifier return a shorter Expires than requested. One hour is
    /// far above any real refresh interval and keeps a single request from pinning a timer and a linked
    /// CTS for days.
    /// </remarks>
    private const int MaxExpiresSeconds = 3600;

    /// <summary>
    /// Concurrent leases one dialog may hold (#158 P2-12).
    /// </summary>
    /// <remarks>
    /// Each (event package, id) pair is its own lease with its own CTS and background delay, and without a
    /// subscription handler every well-formed package is accepted — so the count is peer-controlled.
    /// Thirty-two is generous for a dialog that realistically carries one or two.
    /// </remarks>
    private const int MaxConcurrentLeases = 32;

    private readonly ILogger _logger;
    private readonly Func<SipSubscriptionIdentifier, string, CancellationToken, Task> _onExpiredAsync;
    private readonly object _sync = new();
    private readonly Dictionary<string, SipSubscriptionLease> _activeLeases = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifecycleCts = new();
    private int _disposed;

    /// <summary>
    /// Creates a lifecycle manager bound to one dialog.
    /// </summary>
    public SipSubscriptionLifecycleManager(
        ILogger logger,
        Func<SipSubscriptionIdentifier, string, CancellationToken, Task> onExpiredAsync)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onExpiredAsync = onExpiredAsync ?? throw new ArgumentNullException(nameof(onExpiredAsync));
    }

    /// <summary>
    /// Activates or refreshes one subscription lease.
    /// </summary>
    public SipSubscriptionLifecycleUpdate ActivateOrRefresh(
        SipSubscriptionIdentifier identifier,
        int requestedExpiresSeconds)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipSubscriptionLifecycleManager));
        ArgumentNullException.ThrowIfNull(identifier);
        if (requestedExpiresSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedExpiresSeconds), "requestedExpiresSeconds must be > 0.");

        // #158 P2-12: the peer's Expires was taken verbatim. RFC 6665 §4.2.1 lets the notifier shorten it —
        // and it must here, because every lease pins a linked CTS and a background delay for exactly that
        // long. A peer asking for a year got one.
        var effectiveExpires = Math.Min(requestedExpiresSeconds, MaxExpiresSeconds);
        SipSubscriptionLease? oldLease = null;
        SipSubscriptionLease newLease;
        lock (_sync)
        {
            if (_activeLeases.TryGetValue(identifier.Key, out oldLease))
                _activeLeases.Remove(identifier.Key);
            // #158 P2-12: without a handler every valid event package is accepted, and each (package, id)
            // pair is its own lease. Refreshing an existing one is always allowed — only genuinely new
            // leases are capped, so an established subscription can never be starved out by the limit.
            else if (_activeLeases.Count >= MaxConcurrentLeases)
            {
                throw new InvalidOperationException(
                    $"Subscription lease limit reached ({MaxConcurrentLeases}); refusing '{identifier.Key}'.");
            }

            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            newLease = new SipSubscriptionLease(identifier, effectiveExpires, timeoutCts);
            _activeLeases[identifier.Key] = newLease;
        }

        if (oldLease is not null)
            DisposeLeaseWithLogging(oldLease);
        _ = RunLeaseTimeoutAsync(newLease);

        return new SipSubscriptionLifecycleUpdate
        {
            EffectiveExpiresSeconds = effectiveExpires,
            SubscriptionStateHeader = $"active;expires={effectiveExpires}"
        };
    }

    /// <summary>
    /// Terminates one active subscription (or no-op if unknown) and returns terminated state headers.
    /// </summary>
    public SipSubscriptionLifecycleUpdate Terminate(
        SipSubscriptionIdentifier identifier,
        string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipSubscriptionLifecycleManager));
        ArgumentNullException.ThrowIfNull(identifier);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.", nameof(reason));

        SipSubscriptionLease? lease = null;
        lock (_sync)
        {
            if (_activeLeases.TryGetValue(identifier.Key, out lease))
                _activeLeases.Remove(identifier.Key);
        }

        if (lease is not null)
            DisposeLeaseWithLogging(lease);

        return new SipSubscriptionLifecycleUpdate
        {
            EffectiveExpiresSeconds = 0,
            SubscriptionStateHeader = $"terminated;reason={reason}"
        };
    }

    /// <summary>
    /// Disposes all active subscription leases.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        List<SipSubscriptionLease> toDispose;
        lock (_sync)
        {
            toDispose = _activeLeases.Values.ToList();
            _activeLeases.Clear();
        }

        foreach (var lease in toDispose)
            DisposeLeaseWithLogging(lease);

        try
        {
            _lifecycleCts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP subscription lifecycle cancellation failed.");
        }

        _lifecycleCts.Dispose();
    }

    /// <summary>
    /// Runs timeout wait for one subscription lease and triggers timeout callback on expiry.
    /// </summary>
    private async Task RunLeaseTimeoutAsync(SipSubscriptionLease lease)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(lease.ExpiresSeconds), lease.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
            return;

        var expired = false;
        lock (_sync)
        {
            if (_activeLeases.TryGetValue(lease.Identifier.Key, out var current)
                && ReferenceEquals(current, lease))
            {
                _activeLeases.Remove(lease.Identifier.Key);
                expired = true;
            }
        }

        if (!expired)
            return;

        try
        {
            await _onExpiredAsync(lease.Identifier, "timeout", _lifecycleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected during disposal/session shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to handle SIP subscription timeout notify for {EventHeader}.",
                lease.Identifier.ToEventHeaderValue());
        }
        finally
        {
            DisposeLeaseWithLogging(lease);
        }
    }

    /// <summary>
    /// Disposes one lease with safe logging.
    /// </summary>
    private void DisposeLeaseWithLogging(SipSubscriptionLease lease)
    {
        try
        {
            lease.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed disposing SIP subscription lease for {EventHeader}.",
                lease.Identifier.ToEventHeaderValue());
        }
    }
}
