using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Encoded-media-frame tap for <see cref="SipCoreCallChannel"/>: owns one outbound send delegate and
/// the inbound listener fan-out for a single frame kind (audio or video) on one call leg. Extracted
/// from the channel (formerly the <c>AudioFrames</c>/<c>VideoFrames</c> inline methods and partial) so
/// each frame concern holds its own state and locks instead of sharing the channel's. The SDK is
/// transport-only, so a frame is opaque encoded bytes. The send delegate and the listener list keep
/// separate locks, exactly as the channel did.
/// </summary>
/// <typeparam name="TFrame">The encoded frame type — <c>CallAudioFrame</c> or <c>CallVideoFrame</c>.</typeparam>
internal sealed class SipCallChannelFrameTap<TFrame>
{
    private readonly string _kind;
    private readonly ILogger _logger;
    private readonly object _sendSync = new();
    private readonly object _listenerSync = new();
    private readonly List<Action<TFrame>> _listeners = [];
    private Func<TFrame, CancellationToken, Task>? _sendDelegate;

    /// <param name="kind">Diagnostic label for this frame kind, e.g. <c>"Audio"</c> or <c>"Video"</c>.</param>
    /// <param name="logger">Logger for isolated listener faults.</param>
    public SipCallChannelFrameTap(string kind, ILogger logger)
    {
        _kind = kind;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends one encoded frame through the current send delegate, if any.</summary>
    public Task SendFrameAsync(TFrame frame, CancellationToken ct)
    {
        Func<TFrame, CancellationToken, Task>? send;
        lock (_sendSync) send = _sendDelegate;
        return send is not null ? send(frame, ct) : Task.CompletedTask;
    }

    /// <summary>Fans one inbound frame out to every listener, isolating a listener fault.</summary>
    public void DeliverInbound(TFrame frame)
    {
        Action<TFrame>[] listeners;
        lock (_listenerSync) listeners = [.. _listeners];
        foreach (var listener in listeners)
        {
            try { listener(frame); }
            catch (Exception ex)
            {
                // Isolate one listener's fault from the others and the RTP/callback thread.
                _logger.LogDebug(ex, "{Kind} frame listener threw; continuing with the remaining listeners.", _kind);
            }
        }
    }

    /// <summary>Sets (or clears) the outbound send delegate.</summary>
    public void SetSendDelegate(Func<TFrame, CancellationToken, Task>? sendDelegate)
    {
        lock (_sendSync) _sendDelegate = sendDelegate;
    }

    /// <summary>Registers an inbound-frame listener; a duplicate is ignored.</summary>
    public void AddListener(Action<TFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_listenerSync)
        {
            if (_listeners.Contains(onFrame)) return;
            _listeners.Add(onFrame);
        }
    }

    /// <summary>Removes a previously registered listener.</summary>
    public void RemoveListener(Action<TFrame> onFrame)
    {
        if (onFrame is null) return;
        lock (_listenerSync) _listeners.Remove(onFrame);
    }

    /// <summary>Clears the listeners and the send delegate on channel teardown.</summary>
    public void Dispose()
    {
        lock (_listenerSync) _listeners.Clear();
        lock (_sendSync) _sendDelegate = null;
    }
}
