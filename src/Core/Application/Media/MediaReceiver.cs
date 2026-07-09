using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.Application.Media;

public sealed class MediaReceiver : IMediaReceiver
{
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private Call? _call;
    private Action<CallAudioFrame>? _listener;
    private bool _disposed;

    internal MediaReceiver(ILogger<MediaReceiver>? logger = null)
    {
        _logger = logger ?? NullLogger<MediaReceiver>.Instance;
    }

    public event EventHandler<MediaFrameReceivedEventArgs>? FrameReceived;

    public void AttachToCall(ICall call)
    {
        if (call is not Call sdkCall)
            throw new ArgumentException("Call must be created by this SDK.", nameof(call));

        Action<CallAudioFrame> listener = OnCallAudioFrame;

        Call? previousCall;
        Action<CallAudioFrame>? previousListener;
        lock (_sync)
        {
            ThrowIfDisposed();
            previousCall = _call;
            previousListener = _listener;
            _call = sdkCall;
            _listener = listener;
        }

        if (previousCall != null && previousListener != null)
            previousCall.RemoveAudioFrameListener(previousListener);

        sdkCall.AddAudioFrameListener(listener);

        lock (_sync)
        {
            if (!_disposed &&
                ReferenceEquals(_call, sdkCall) &&
                ReferenceEquals(_listener, listener))
            {
                return;
            }
        }

        sdkCall.RemoveAudioFrameListener(listener);
    }

    public void Detach()
    {
        Call? call;
        Action<CallAudioFrame>? listener;
        lock (_sync)
        {
            call = _call;
            listener = _listener;
            _call = null;
            _listener = null;
        }

        if (call != null && listener != null)
            call.RemoveAudioFrameListener(listener);
    }

    private void OnCallAudioFrame(CallAudioFrame frame)
    {
        var mediaFrame = new MediaFrame(frame.Payload, frame.PayloadType, frame.DurationRtpUnits);
        var args = new MediaFrameReceivedEventArgs(mediaFrame);

        var handlers = FrameReceived;
        if (handlers == null) return;

        foreach (EventHandler<MediaFrameReceivedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // Isolate one subscriber's fault from the others and the RTP callback thread.
                _logger.LogDebug(ex, "Media frame subscriber threw; continuing with the remaining subscribers.");
            }
        }
    }

    public void Dispose()
    {
        Call? call;
        Action<CallAudioFrame>? listener;
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
            call.RemoveAudioFrameListener(listener);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MediaReceiver));
    }
}
