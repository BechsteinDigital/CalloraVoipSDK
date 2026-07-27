using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Per-SSRC RFC 3550 §A.1 sequence validators for one RTP session, keyed by SSRC. The table is capped and
/// LRU-evicted so a peer spoofing a stream of distinct SSRCs cannot grow it without bound (memory DoS); a real
/// session only ever sees a handful of SSRCs. Not thread-safe: mutation (<see cref="GetOrAdd"/>) is confined to
/// the single receive-loop thread; <see cref="Count"/> / <see cref="Contains"/> are read-only diagnostic seams.
/// </summary>
internal sealed class RtpTrackedSsrcTable
{
    private const int MaxTrackedSsrcs = 64;

    private readonly Dictionary<uint, RtpTrackedSsrc> _validators = new();
    private readonly ILogger _logger;
    private long _activityClock;

    /// <summary>Creates an empty table logging eviction diagnostics to <paramref name="logger"/>.</summary>
    public RtpTrackedSsrcTable(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Number of currently tracked SSRCs.</summary>
    public int Count => _validators.Count;

    /// <summary>True when <paramref name="ssrc"/> already has a validator.</summary>
    public bool Contains(uint ssrc) => _validators.ContainsKey(ssrc);

    /// <summary>
    /// Returns the sequence validator for <paramref name="ssrc"/>, creating one on first sight (evicting the
    /// least-recently-active SSRC first if the cap is reached) and bumping the SSRC's recency.
    /// </summary>
    public RtpTrackedSsrc GetOrAdd(uint ssrc)
    {
        if (!_validators.TryGetValue(ssrc, out var tracked))
        {
            if (_validators.Count >= MaxTrackedSsrcs)
                Evict();

            tracked = new RtpTrackedSsrc(new RtpSequenceValidator(), ++_activityClock);
            _validators[ssrc] = tracked;
        }
        else
        {
            tracked.LastActivity = ++_activityClock;
        }

        return tracked;
    }

    // Removes the least-recently-active SSRC so the table stays bounded. Runs only when the cap is reached.
    private void Evict()
    {
        uint evictKey = 0;
        var oldestActivity = long.MaxValue;
        foreach (var entry in _validators)
        {
            if (entry.Value.LastActivity < oldestActivity)
            {
                oldestActivity = entry.Value.LastActivity;
                evictKey = entry.Key;
            }
        }

        _validators.Remove(evictKey);
        _logger.LogDebug(
            "RTP validator table reached {Max} SSRCs; evicted least-recently-active SSRC={Ssrc:X8}.",
            MaxTrackedSsrcs,
            evictKey);
    }
}
