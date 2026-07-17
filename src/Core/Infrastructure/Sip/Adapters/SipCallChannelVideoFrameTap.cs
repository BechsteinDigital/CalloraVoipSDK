using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Encoded-video-frame tap for <see cref="SipCoreCallChannel"/>: owns the outbound send delegate and
/// the inbound listener fan-out for one call leg. Extracted from the channel (formerly the
/// <c>SipCoreCallChannel.VideoFrames</c> partial) into its own collaborator so the video-frame concern
/// holds its own state and locks instead of sharing the channel's. The SDK is transport-only, so a
/// frame is opaque encoded bytes; this mirrors the audio-frame tap. The send delegate and the listener
/// list keep separate locks, exactly as the partial did.
/// </summary>
internal sealed class SipCallChannelVideoFrameTap
{
    private readonly ILogger _logger;
    private readonly object _sendSync = new();
    private readonly object _listenerSync = new();
    private readonly List<Action<CallVideoFrame>> _listeners = [];
    private Func<CallVideoFrame, CancellationToken, Task>? _sendDelegate;

    public SipCallChannelVideoFrameTap(ILogger logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Sends one encoded video frame through the current send delegate, if any.</summary>
    public Task SendFrameAsync(CallVideoFrame frame, CancellationToken ct)
    {
        Func<CallVideoFrame, CancellationToken, Task>? send;
        lock (_sendSync) send = _sendDelegate;
        return send is not null ? send(frame, ct) : Task.CompletedTask;
    }

    /// <summary>Fans one inbound frame out to every listener, isolating a listener fault.</summary>
    public void DeliverInbound(CallVideoFrame frame)
    {
        Action<CallVideoFrame>[] listeners;
        lock (_listenerSync) listeners = [.. _listeners];
        foreach (var listener in listeners)
        {
            try { listener(frame); }
            catch (Exception ex)
            {
                // Isolate one listener's fault from the others and the RTP/callback thread.
                _logger.LogDebug(ex, "Video frame listener threw; continuing with the remaining listeners.");
            }
        }
    }

    /// <summary>Sets (or clears) the outbound send delegate.</summary>
    public void SetSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate)
    {
        lock (_sendSync) _sendDelegate = sendDelegate;
    }

    /// <summary>Registers an inbound-frame listener; a duplicate is ignored.</summary>
    public void AddListener(Action<CallVideoFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_listenerSync)
        {
            if (_listeners.Contains(onFrame)) return;
            _listeners.Add(onFrame);
        }
    }

    /// <summary>Removes a previously registered listener.</summary>
    public void RemoveListener(Action<CallVideoFrame> onFrame)
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
