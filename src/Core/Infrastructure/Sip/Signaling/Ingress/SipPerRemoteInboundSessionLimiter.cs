using System.Collections.Concurrent;
using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// #158 P1-5 (per-remote session cap): bounds concurrent inbound dialog sessions per source IP address so a
/// single remote cannot consume the whole global inbound-session budget. The global cap
/// (<c>SipCallSignalingService</c>) limits total sessions; this limiter additionally fair-shares that budget
/// across source addresses. Admission reserves a slot keyed by Call-ID and is released once — on dialog
/// termination or a failed session insert.
/// </summary>
internal sealed class SipPerRemoteInboundSessionLimiter
{
    /// <summary>
    /// Default per-remote concurrent inbound session ceiling.
    /// </summary>
    public const int DefaultMaxPerRemote = 32;

    private readonly int _maxPerRemote;
    private readonly ConcurrentDictionary<IPAddress, int> _counts = new();
    private readonly ConcurrentDictionary<string, IPAddress> _sessionRemotes = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a per-remote inbound session limiter.
    /// </summary>
    /// <param name="maxPerRemote">Concurrent inbound sessions allowed per source IP; non-positive falls back to
    /// the default.</param>
    public SipPerRemoteInboundSessionLimiter(int? maxPerRemote = null)
    {
        _maxPerRemote = maxPerRemote is { } value && value > 0 ? value : DefaultMaxPerRemote;
    }

    /// <summary>
    /// Reserves one inbound-session slot for <paramref name="remote"/>, keyed by <paramref name="callId"/>.
    /// Returns false when the per-remote ceiling is reached (the caller should reject the request). A slot
    /// reserved here must be released exactly once via <see cref="Release"/>.
    /// </summary>
    public bool TryAdmit(string callId, IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(remote);

        while (true)
        {
            if (_counts.TryGetValue(remote, out var current))
            {
                if (current >= _maxPerRemote)
                    return false;
                if (!_counts.TryUpdate(remote, current + 1, current))
                    continue;
            }
            else if (!_counts.TryAdd(remote, 1))
            {
                continue;
            }

            _sessionRemotes[callId] = remote;
            return true;
        }
    }

    /// <summary>
    /// Releases the slot previously admitted for <paramref name="callId"/>. No-op for an unknown Call-ID, so it
    /// is safe to call unconditionally on dialog termination.
    /// </summary>
    public void Release(string callId)
    {
        if (callId is null || !_sessionRemotes.TryRemove(callId, out var remote))
            return;

        while (true)
        {
            if (!_counts.TryGetValue(remote, out var current))
                return;
            if (current <= 1)
            {
                // Remove the entry atomically only if it still holds exactly this value, so a concurrent admit
                // that just incremented it is not lost.
                if (((ICollection<KeyValuePair<IPAddress, int>>)_counts)
                    .Remove(new KeyValuePair<IPAddress, int>(remote, current)))
                {
                    return;
                }
            }
            else if (_counts.TryUpdate(remote, current - 1, current))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Clears all reservations (service disposal).
    /// </summary>
    public void Clear()
    {
        _sessionRemotes.Clear();
        _counts.Clear();
    }

    /// <summary>
    /// Current reserved session count for one remote (diagnostics/tests).
    /// </summary>
    internal int CountFor(IPAddress remote) => _counts.TryGetValue(remote, out var current) ? current : 0;
}
