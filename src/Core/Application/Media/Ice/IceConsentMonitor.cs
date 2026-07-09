using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// Runs the RFC 7675 consent-freshness loop for a nominated ICE pair: periodically sends a STUN
/// consent check and, when no check has been answered within the consent lifetime
/// (<see cref="IceConsentFreshnessPolicy.ConsentExpiry"/>), raises consent loss so the caller can
/// stop transmitting on the pair. The clock, delay and randomness are injected so the loop is
/// deterministically testable; production wires them to the runtime.
/// </summary>
internal sealed class IceConsentMonitor : IAsyncDisposable
{
    private readonly IceConsentFreshnessPolicy _policy;
    private readonly Func<CancellationToken, Task<bool>> _sendConsentCheck;
    private readonly Action _onConsentLost;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _nextRandom;
    private readonly ILogger<IceConsentMonitor> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates a consent monitor. <paramref name="sendConsentCheck"/> sends one consent check and
    /// returns <see langword="true"/> when it was answered; <paramref name="onConsentLost"/> is
    /// invoked once when consent expires.
    /// </summary>
    public IceConsentMonitor(
        IceConsentFreshnessPolicy policy,
        Func<CancellationToken, Task<bool>> sendConsentCheck,
        Action onConsentLost,
        ILoggerFactory loggerFactory,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? nextRandom = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sendConsentCheck = sendConsentCheck ?? throw new ArgumentNullException(nameof(sendConsentCheck));
        _onConsentLost = onConsentLost ?? throw new ArgumentNullException(nameof(onConsentLost));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<IceConsentMonitor>();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _nextRandom = nextRandom ?? Random.Shared.NextDouble;
    }

    /// <summary>Starts the consent loop. Idempotent and thread-safe — a second call, or a call
    /// after disposal, is a no-op.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
                return;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastConfirmed = _utcNow();

        while (!ct.IsCancellationRequested)
        {
            bool answered;
            try
            {
                await _delay(_policy.NextCheckDelay(_nextRandom()), ct).ConfigureAwait(false);
                answered = await _sendConsentCheck(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("ICE consent monitor stopped.");
                return;
            }

            if (answered)
            {
                lastConfirmed = _utcNow();
                continue;
            }

            if (!_policy.IsConsentFresh(lastConfirmed, _utcNow()))
            {
                _logger.LogWarning(
                    "ICE consent expired: no consent check answered within {Expiry}.",
                    IceConsentFreshnessPolicy.ConsentExpiry);
                _onConsentLost();
                return;
            }
        }
    }

    /// <summary>Cancels the loop and awaits its completion. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
