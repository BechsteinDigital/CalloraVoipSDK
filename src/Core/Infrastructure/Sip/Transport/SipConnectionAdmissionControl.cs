using System.Collections.Concurrent;
using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Admits inbound connection-oriented SIP connections against a global cap and a per-source-IP cap, so no
/// remote peer can pin an unbounded number of accepted connections (#158 P1-3). An admitted connection holds
/// a lease for its entire lifetime; disposing the lease — from the connection's close callback — frees the
/// global and per-remote slots. Thread-safe: admission and release are serialised under a single lock, off
/// the media hot path.
/// </summary>
internal sealed class SipConnectionAdmissionControl
{
    private readonly int _maxGlobal;
    private readonly int _maxPerRemote;
    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, int> _perRemote = new();
    private int _total;

    /// <summary>
    /// Creates an admission control with a global and per-remote cap. A non-positive cap disables that
    /// dimension (unlimited).
    /// </summary>
    public SipConnectionAdmissionControl(int maxGlobal, int maxPerRemote)
    {
        _maxGlobal = maxGlobal;
        _maxPerRemote = maxPerRemote;
    }

    /// <summary>
    /// Tries to admit one connection from <paramref name="remote"/>. Returns a lease that must be disposed
    /// when the connection closes, or <c>null</c> when the global or per-remote cap is reached.
    /// </summary>
    public IDisposable? TryAdmit(IPAddress remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        lock (_sync)
        {
            if (_maxGlobal > 0 && _total >= _maxGlobal)
                return null;

            var current = _perRemote.TryGetValue(remote, out var count) ? count : 0;
            if (_maxPerRemote > 0 && current >= _maxPerRemote)
                return null;

            _total++;
            _perRemote[remote] = current + 1;
            return new SipConnectionAdmissionLease(() => Release(remote));
        }
    }

    private void Release(IPAddress remote)
    {
        lock (_sync)
        {
            if (_total > 0)
                _total--;

            if (_perRemote.TryGetValue(remote, out var count))
            {
                if (count <= 1)
                    _perRemote.Remove(remote);
                else
                    _perRemote[remote] = count - 1;
            }
        }
    }
}
