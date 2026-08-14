using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Maintains RFC4028 session timer runtime behavior for one SIP dialog.
/// Schedules local refreshes when this side is refresher and enforces expiry when peer is refresher.
/// </summary>
internal sealed class SipSessionTimerManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<bool>> _sendRefreshAsync;
    private readonly Func<CancellationToken, Task> _onExpiredAsync;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifecycleCts = new();

    private CancellationTokenSource? _scheduleCts;
    private int _sessionIntervalSeconds;
    private bool _localRefresher;
    private bool _isActive;
    private int _disposed;

    /// <summary>
    /// Creates a timer manager for one call session.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock the refresh/expiry schedule waits on. Defaults to <see cref="TimeProvider.System"/>, i.e. real
    /// time. A test can pass a fake provider to advance an RFC 4028 session interval — up to 90 minutes on the
    /// wire — instantly, which is the only way to soak many timer cycles without waiting for them (F003).
    /// </param>
    public SipSessionTimerManager(
        ILogger logger,
        Func<CancellationToken, Task<bool>> sendRefreshAsync,
        Func<CancellationToken, Task> onExpiredAsync,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sendRefreshAsync = sendRefreshAsync ?? throw new ArgumentNullException(nameof(sendRefreshAsync));
        _onExpiredAsync = onExpiredAsync ?? throw new ArgumentNullException(nameof(onExpiredAsync));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Applies negotiated session timer values and (re)schedules runtime timers.
    /// </summary>
    public void ApplyNegotiation(int intervalSeconds, bool localRefresher)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (intervalSeconds <= 0)
            return;

        lock (_sync)
        {
            _sessionIntervalSeconds = intervalSeconds;
            _localRefresher = localRefresher;
            _isActive = true;
            RescheduleLocked();
        }
    }

    /// <summary>
    /// Stops all active timers for this dialog.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            _isActive = false;
            CancelScheduleLocked();
        }
    }

    /// <summary>
    /// Schedules next refresh/expiry timer based on current negotiated role.
    /// </summary>
    private void RescheduleLocked()
    {
        CancelScheduleLocked();
        if (!_isActive)
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        _scheduleCts = cts;
        _ = RunScheduleAsync(
            _sessionIntervalSeconds,
            _localRefresher,
            cts.Token);
    }

    /// <summary>
    /// Cancels current schedule token and disposes it.
    /// </summary>
    private void CancelScheduleLocked()
    {
        var cts = _scheduleCts;
        _scheduleCts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP session timer cancellation failed.");
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Runs one scheduled cycle and either refreshes or expires the dialog.
    /// </summary>
    private async Task RunScheduleAsync(
        int intervalSeconds,
        bool localRefresher,
        CancellationToken ct)
    {
        try
        {
            var delay = localRefresher
                ? ComputeRefreshDelay(intervalSeconds)
                : ComputeExpiryDelay(intervalSeconds);
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                return;

            if (localRefresher)
            {
                var refreshed = await TrySendRefreshAsync(ct).ConfigureAwait(false);
                if (refreshed)
                {
                    ApplyNegotiation(intervalSeconds, localRefresher: true);
                    return;
                }

                await TriggerExpiredAsync("local session refresh failed").ConfigureAwait(false);
                return;
            }

            await TriggerExpiredAsync("remote refresher timeout").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected when dialog or schedule is reset/disposed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session timer loop failed unexpectedly.");
            await TriggerExpiredAsync("session timer loop failure").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes one local refresh attempt and logs transport/protocol errors.
    /// </summary>
    private async Task<bool> TrySendRefreshAsync(CancellationToken ct)
    {
        try
        {
            return await _sendRefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session refresh dispatch failed.");
            return false;
        }
    }

    /// <summary>
    /// Triggers session expiration callback exactly once per active timer lifecycle.
    /// </summary>
    private async Task TriggerExpiredAsync(string reason)
    {
        lock (_sync)
        {
            if (!_isActive)
                return;

            _isActive = false;
            CancelScheduleLocked();
        }

        _logger.LogWarning("SIP session timer expired: {Reason}.", reason);

        try
        {
            await _onExpiredAsync(_lifecycleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown/dispose
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SIP session expiration callback failed.");
        }
    }

    /// <summary>
    /// Computes local refresher dispatch delay (about two-thirds of negotiated interval).
    /// </summary>
    private static TimeSpan ComputeRefreshDelay(int intervalSeconds)
    {
        var safetyMargin = Math.Clamp(intervalSeconds / 3, 5, 600);
        var refreshAt = Math.Max(1, intervalSeconds - safetyMargin);
        return TimeSpan.FromSeconds(refreshAt);
    }

    /// <summary>
    /// Computes remote refresher timeout delay with small grace for network jitter.
    /// </summary>
    private static TimeSpan ComputeExpiryDelay(int intervalSeconds)
    {
        var grace = Math.Clamp(intervalSeconds / 10, 2, 30);
        return TimeSpan.FromSeconds(intervalSeconds + grace);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
        try
        {
            _lifecycleCts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP session timer lifecycle cancellation failed.");
        }

        _lifecycleCts.Dispose();
    }
}
