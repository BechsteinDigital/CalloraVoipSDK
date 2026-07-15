using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Encoded-video-frame tap for <see cref="SipCoreCallChannel"/> (send delegate, inbound listener
/// fan-out). Kept in this partial file so the channel stays within the per-file size limit; the
/// shared media state (locks, listener list, send delegate) lives with the channel's other fields.
/// Mirrors the audio-frame tap exactly — the SDK is transport-only and treats a video frame as
/// opaque encoded bytes.
/// </summary>
internal sealed partial class SipCoreCallChannel
{
    /// <inheritdoc />
    public Task SendVideoFrameAsync(CallVideoFrame frame, CancellationToken ct = default)
    {
        Func<CallVideoFrame, CancellationToken, Task>? send;
        lock (_callbackSync) send = _videoSendDelegate;
        return send is not null ? send(frame, ct) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public void DeliverInboundVideoFrame(CallVideoFrame frame)
    {
        Action<CallVideoFrame>[] listeners;
        lock (_videoSync) listeners = [.. _videoListeners];
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

    /// <inheritdoc />
    public void SetVideoSendDelegate(Func<CallVideoFrame, CancellationToken, Task>? sendDelegate)
    {
        lock (_callbackSync) _videoSendDelegate = sendDelegate;
    }

    /// <inheritdoc />
    public void AddVideoFrameListener(Action<CallVideoFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SipCoreCallChannel));

        lock (_videoSync)
        {
            if (_videoListeners.Contains(onFrame)) return;
            _videoListeners.Add(onFrame);
        }
    }

    /// <inheritdoc />
    public void RemoveVideoFrameListener(Action<CallVideoFrame> onFrame)
    {
        if (onFrame == null) return;
        lock (_videoSync) _videoListeners.Remove(onFrame);
    }
}
