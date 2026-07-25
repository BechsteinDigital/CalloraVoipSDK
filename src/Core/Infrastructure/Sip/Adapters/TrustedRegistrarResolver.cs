using System.Net;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Resolves and caches the IP addresses of a line's trusted SIP registrar/proxy hosts for inbound-peer matching,
/// without ever blocking the inbound dispatch thread on DNS. The lookup runs on a one-shot background task; a read
/// returns the warm cache or an empty set while resolution is in flight (best-effort — empty contributes nothing).
/// A resolution that produced no address (a transient DNS failure) is not cached and is retried after a bounded
/// back-off, so a line is never left permanently without trusted peers. The DNS resolver and clock are injectable
/// for deterministic testing.
/// </summary>
internal sealed class TrustedRegistrarResolver
{
    private static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(30);
    // After this many consecutive failed resolutions a host is treated as permanently unresolvable and the empty
    // result is cached, so reads stop re-attempting. A transient failure recovers well before this.
    private const int MaxResolveAttempts = 5;

    private readonly IReadOnlyList<string> _hosts;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<long> _nowTicks;
    private readonly long _retryBackoffMs;
    private readonly ILogger _logger;

    private volatile IReadOnlyCollection<IPAddress>? _cached;
    private int _inFlight;
    private long _retryAfterTicks;
    private int _failedAttempts; // only touched inside the _inFlight-guarded ResolveAsync

    /// <summary>
    /// Creates a resolver for the given (already bare) host names. <paramref name="resolve"/> defaults to
    /// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> and <paramref name="nowTicks"/> to
    /// <see cref="Environment.TickCount64"/>; both are injectable for testing.
    /// </summary>
    public TrustedRegistrarResolver(
        IReadOnlyList<string> hosts,
        ILogger logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<long>? nowTicks = null,
        TimeSpan? retryBackoff = null)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _nowTicks = nowTicks ?? (() => Environment.TickCount64);
        _retryBackoffMs = (long)(retryBackoff ?? DefaultRetryBackoff).TotalMilliseconds;
    }

    /// <summary>
    /// Non-blocking read of the trusted registrar addresses. Returns the warm cache, or an empty set while a
    /// background resolution runs (or is backing off after a failure) — the DNS lookup never runs on the caller's
    /// thread.
    /// </summary>
    public IReadOnlyCollection<IPAddress> Addresses()
    {
        var cached = _cached;
        if (cached is not null)
            return cached;

        MaybeStartResolution();
        return Array.Empty<IPAddress>();
    }

    /// <summary>Warms the cache in the background (e.g. from the registration loop); never blocks.</summary>
    public void Warm() => MaybeStartResolution();

    private void MaybeStartResolution()
    {
        if (_cached is not null)
            return;
        if (_hosts.Count == 0)
        {
            // Nothing to resolve — cache an intentionally empty set so reads short-circuit and never re-attempt.
            _cached = Array.Empty<IPAddress>();
            return;
        }
        if (_nowTicks() < Volatile.Read(ref _retryAfterTicks))
            return; // still within the post-failure back-off window
        if (Interlocked.Exchange(ref _inFlight, 1) != 0)
            return; // a resolution is already running

        _ = ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        try
        {
            var addresses = new HashSet<IPAddress>();
            foreach (var host in _hosts)
            {
                try
                {
                    foreach (var address in await _resolve(host, CancellationToken.None).ConfigureAwait(false))
                        addresses.Add(address);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not resolve trusted registrar host '{Host}' for inbound matching.", host);
                }
            }

            if (addresses.Count > 0)
            {
                _cached = addresses;
            }
            else if (++_failedAttempts >= MaxResolveAttempts)
            {
                // Give up after a bounded number of retries: cache the empty set so a permanently unresolvable
                // host stops re-attempting on every read (best-effort matching contributes nothing anyway).
                _cached = addresses;
            }
            else
            {
                // Transient failure: schedule a retry after the back-off rather than caching the empty result.
                Volatile.Write(ref _retryAfterTicks, _nowTicks() + _retryBackoffMs);
            }
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }
}
