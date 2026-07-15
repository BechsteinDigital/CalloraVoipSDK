using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Surfaces a call's inbound encoded video frames to the application via the
/// <see cref="FrameReceived"/> event. Attach to a call to start receiving; a faulting subscriber is
/// isolated so it neither stops other subscribers nor the RTP callback. Mirrors
/// <see cref="MediaReceiver"/> for video — transport-only, so the payload is encoded, not decoded.
/// </summary>
public sealed class VideoReceiver : IVideoReceiver
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private Call? _call;
    private Action<CallVideoFrame>? _listener;
    private bool _disposed;

    internal VideoReceiver(ILogger<VideoReceiver>? logger = null)
    {
        _logger = logger ?? NullLogger<VideoReceiver>.Instance;
    }

    /// <summary>
    /// Raised for each reassembled inbound video frame on the attached call. Fires on the RTP receive
    /// thread, so keep handlers fast and thread-safe; exceptions thrown by a handler are caught and logged.
    /// </summary>
    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    /// <summary>Attaches this receiver to <paramref name="call"/>; replaces and detaches any previous attachment.</summary>
    /// <param name="call">The call to receive inbound video from. Must be a call created by this SDK.</param>
    /// <exception cref="ArgumentException"><paramref name="call"/> was not created by this SDK.</exception>
    /// <exception cref="ObjectDisposedException">The receiver has been disposed.</exception>
    public void AttachToCall(ICall call)
    {
        if (call is not Call sdkCall)
            throw new ArgumentException("Call must be created by this SDK.", nameof(call));

        Action<CallVideoFrame> listener = OnCallVideoFrame;

        Call? previousCall;
        Action<CallVideoFrame>? previousListener;
        lock (_sync)
        {
            ThrowIfDisposed();
            previousCall = _call;
            previousListener = _listener;
            _call = sdkCall;
            _listener = listener;
        }

        if (previousCall != null && previousListener != null)
            previousCall.RemoveVideoFrameListener(previousListener);

        sdkCall.AddVideoFrameListener(listener);

        lock (_sync)
        {
            if (!_disposed &&
                ReferenceEquals(_call, sdkCall) &&
                ReferenceEquals(_listener, listener))
            {
                return;
            }
        }

        sdkCall.RemoveVideoFrameListener(listener);
    }

    /// <summary>Detaches from the current call; <see cref="FrameReceived"/> stops firing until re-attached.</summary>
    public void Detach()
    {
        Call? call;
        Action<CallVideoFrame>? listener;
        lock (_sync)
        {
            call = _call;
            listener = _listener;
            _call = null;
            _listener = null;
        }

        if (call != null && listener != null)
            call.RemoveVideoFrameListener(listener);
    }

    private void OnCallVideoFrame(CallVideoFrame frame)
    {
        var videoFrame = new VideoFrame(frame.Payload, frame.PayloadType, frame.RtpTimestamp, frame.IsKeyFrame);
        var args = new VideoFrameReceivedEventArgs(videoFrame);

        var handlers = FrameReceived;
        if (handlers == null) return;

        foreach (EventHandler<VideoFrameReceivedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // Isolate one subscriber's fault from the others and the RTP callback thread.
                _logger.LogDebug(ex, "Video frame subscriber threw; continuing with the remaining subscribers.");
            }
        }
    }

    /// <summary>Detaches and disposes the receiver; <see cref="FrameReceived"/> will no longer fire.</summary>
    public void Dispose()
    {
        Call? call;
        Action<CallVideoFrame>? listener;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            call = _call;
            listener = _listener;
            _call = null;
            _listener = null;
        }

        if (call != null && listener != null)
            call.RemoveVideoFrameListener(listener);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoReceiver));
    }
}
