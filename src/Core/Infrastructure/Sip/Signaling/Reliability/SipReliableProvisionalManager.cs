using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Manages reliable provisional INVITE responses (RFC3262) and PRACK correlation.
/// </summary>
internal sealed class SipReliableProvisionalManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<int, SipReliableProvisionalEntry> _pendingByRseq = new();
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly Random _random = new();
    private int _lastRseq;
    private int _disposed;

    /// <summary>
    /// Creates a new reliable provisional manager for one dialog.
    /// </summary>
    public SipReliableProvisionalManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lastRseq = _random.Next(1, int.MaxValue / 2);
    }

    /// <summary>
    /// Registers one reliable provisional INVITE response and returns generated RSeq.
    /// </summary>
    public int RegisterPendingInviteProvisional(int inviteCseq)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipReliableProvisionalManager));
        if (inviteCseq <= 0)
            throw new ArgumentOutOfRangeException(nameof(inviteCseq), "inviteCseq must be > 0.");

        lock (_sync)
        {
            var rseq = NextRseqLocked();
            _pendingByRseq[rseq] = new SipReliableProvisionalEntry(rseq, inviteCseq);
            return rseq;
        }
    }

    /// <summary>
    /// Waits for PRACK acknowledgment for one registered reliable provisional response.
    /// Returns false when timeout/disposal occurs before PRACK is received.
    /// </summary>
    public async Task<bool> WaitForPrackAsync(
        int rseq,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive.");

        SipReliableProvisionalEntry? entry;
        lock (_sync)
            _pendingByRseq.TryGetValue(rseq, out entry);
        if (entry is null)
            return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifecycleCts.Token);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await entry.WaitForPrackAsync(timeoutCts.Token).ConfigureAwait(false);
            RemovePending(rseq);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Timed out waiting for PRACK for reliable provisional RSeq={RSeq}.", rseq);
            RemovePending(rseq);
            return false;
        }
    }

    /// <summary>
    /// Validates and acknowledges inbound RAck header for a PRACK request.
    /// </summary>
    public bool TryAcknowledge(
        string? rackHeader,
        out int rejectionStatusCode,
        out string rejectionReasonPhrase)
    {
        rejectionStatusCode = 0;
        rejectionReasonPhrase = string.Empty;

        if (!TryParseRack(rackHeader, out var rseq, out var inviteCseq, out var inviteMethod))
        {
            rejectionStatusCode = 400;
            rejectionReasonPhrase = "Bad Request";
            return false;
        }

        if (!inviteMethod.Equals("INVITE", StringComparison.OrdinalIgnoreCase))
        {
            rejectionStatusCode = 481;
            rejectionReasonPhrase = "Call/Transaction Does Not Exist";
            return false;
        }

        SipReliableProvisionalEntry? entry;
        lock (_sync)
        {
            if (!_pendingByRseq.TryGetValue(rseq, out entry))
            {
                rejectionStatusCode = 481;
                rejectionReasonPhrase = "Call/Transaction Does Not Exist";
                return false;
            }

            if (entry.InviteCSeq != inviteCseq)
            {
                rejectionStatusCode = 481;
                rejectionReasonPhrase = "Call/Transaction Does Not Exist";
                return false;
            }
        }

        entry.TryAcknowledge();
        return true;
    }

    /// <summary>
    /// Cancels all pending reliable provisional waits.
    /// </summary>
    public void ClearPending()
    {
        SipReliableProvisionalEntry[] pending;
        lock (_sync)
        {
            pending = _pendingByRseq.Values.ToArray();
            _pendingByRseq.Clear();
        }

        foreach (var entry in pending)
            entry.Cancel();
    }

    /// <summary>
    /// Removes one pending entry by RSeq if present.
    /// </summary>
    private void RemovePending(int rseq)
    {
        SipReliableProvisionalEntry? removed = null;
        lock (_sync)
        {
            if (_pendingByRseq.TryGetValue(rseq, out removed))
                _pendingByRseq.Remove(rseq);
        }

        removed?.Cancel();
    }

    /// <summary>
    /// Generates next non-zero RSeq value.
    /// </summary>
    private int NextRseqLocked()
    {
        unchecked
        {
            _lastRseq++;
            if (_lastRseq <= 0)
                _lastRseq = 1;
            return _lastRseq;
        }
    }

    /// <summary>
    /// Parses RAck header value into rseq/cseq/method tuple.
    /// </summary>
    private static bool TryParseRack(
        string? rackHeader,
        out int rseq,
        out int inviteCseq,
        out string inviteMethod)
    {
        rseq = 0;
        inviteCseq = 0;
        inviteMethod = string.Empty;
        if (string.IsNullOrWhiteSpace(rackHeader))
            return false;

        var parts = rackHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[0], out rseq) || rseq <= 0)
            return false;
        if (!int.TryParse(parts[1], out inviteCseq) || inviteCseq <= 0)
            return false;

        inviteMethod = parts[2].Trim();
        return inviteMethod.Length > 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ClearPending();
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reliable provisional lifecycle cancellation failed.");
        }

        _lifecycleCts.Dispose();
    }
}
