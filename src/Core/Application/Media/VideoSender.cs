using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Feeds application-supplied encoded video frames into a call's outbound RTP stream. Attach to a
/// call, then push frames with <see cref="SendAsync"/>. Frames sent while the call is not
/// connected/on-hold are silently dropped rather than throwing. Mirrors <see cref="MediaSender"/> for
/// video — transport-only, so the payload must already be encoded.
/// </summary>
public sealed class VideoSender : IVideoSender
{
    private readonly object _sync = new();
    private readonly ILogger<VideoSender> _logger;
    private Call? _call;
    private bool _disposed;

    internal VideoSender(ILogger<VideoSender>? logger = null)
    {
        _logger = logger ?? NullLogger<VideoSender>.Instance;
    }

    /// <summary>Attaches this sender to <paramref name="call"/>; replaces any previous attachment.</summary>
    /// <param name="call">The call to send outbound video to. Must be a call created by this SDK.</param>
    /// <exception cref="ArgumentException"><paramref name="call"/> was not created by this SDK.</exception>
    /// <exception cref="ObjectDisposedException">The sender has been disposed.</exception>
    public void AttachToCall(ICall call)
    {
        if (call is not Call sdkCall)
            throw new ArgumentException("Call must be created by this SDK.", nameof(call));

        lock (_sync)
        {
            ThrowIfDisposed();
            _call = sdkCall;
        }
    }

    /// <summary>Detaches from the current call; subsequent <see cref="SendAsync"/> calls will fail until re-attached.</summary>
    public void Detach()
    {
        lock (_sync) _call = null;
    }

    /// <summary>
    /// Sends one encoded video frame to the attached call. Frames are dropped without error when the
    /// call is not in <see cref="CallState.Connected"/> or <see cref="CallState.OnHold"/>.
    /// </summary>
    /// <param name="frame">The encoded video frame (payload, payload type, RTP timestamp, keyframe flag) to send.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <exception cref="InvalidOperationException">No call is attached.</exception>
    /// <exception cref="ObjectDisposedException">The sender has been disposed.</exception>
    public Task SendAsync(VideoFrame frame, CancellationToken ct = default)
    {
        Call? call;
        lock (_sync)
        {
            ThrowIfDisposed();
            call = _call;
        }

        if (call == null)
            throw new InvalidOperationException("Video sender is not attached to a call.");

        // Media callbacks can still race with call teardown; drop late frames instead
        // of surfacing process-terminating exceptions from real-time threads.
        var state = call.State;
        if (state is not (CallState.Connected or CallState.OnHold))
            return Task.CompletedTask;

        var payload = GetPayloadArray(frame.Payload);
        var outbound = new CallVideoFrame(payload, frame.PayloadType, frame.RtpTimestamp, frame.IsKeyFrame);
        try
        {
            return call.SendVideoFrameAsync(outbound, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(
                ex,
                "Dropping outbound video frame because call {CallId} is no longer send-capable (state={State}).",
                call.CallId,
                call.State);
            return Task.CompletedTask;
        }
    }

    /// <summary>Detaches and disposes the sender; further sends throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _call = null;
        }
    }

    private static byte[] GetPayloadArray(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) &&
            segment.Array != null)
        {
            if (segment.Offset == 0 && segment.Count == segment.Array.Length)
                return segment.Array;

            var copy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return copy;
        }

        return payload.ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoSender));
    }
}
