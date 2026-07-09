using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// One-way media forwarding link between a receiver and sender.
/// </summary>
internal sealed class MediaConnection : IDisposable
{
    private readonly IMediaReceiver _receiver;
    private readonly IMediaSender _sender;
    private readonly Channel<MediaFrame> _queue;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _pumpTask;
    private readonly ILogger _logger;
    private int _disposed;

    /// <summary>
    /// Creates a buffered media forwarding link.
    /// </summary>
    internal MediaConnection(
        IMediaReceiver receiver,
        IMediaSender sender,
        int queueCapacity,
        ILogger? logger = null)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger.Instance;

        _queue = Channel.CreateBounded<MediaFrame>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        _receiver.FrameReceived += OnFrameReceived;
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Buffers one inbound frame for asynchronous forwarding.
    /// </summary>
    private void OnFrameReceived(object? _, MediaFrameReceivedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _queue.Writer.TryWrite(e.Frame);
    }

    /// <summary>
    /// Pumps buffered frames to the sender until shutdown.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var frame))
                    await ForwardAsync(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            _logger.LogTrace("Media forwarding pump cancelled during shutdown.");
        }
        catch (ChannelClosedException)
        {
            _logger.LogTrace("Media forwarding queue closed; pump stopping.");
        }
    }

    /// <summary>
    /// Forwards one frame to the destination sender.
    /// </summary>
    private async Task ForwardAsync(MediaFrame frame)
    {
        try
        {
            await _sender.SendAsync(frame, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            _logger.LogTrace("Media frame forwarding cancelled during shutdown.");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "Media frame dropped: link is shutting down.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogTrace(ex, "Media frame dropped: sender is detached.");
        }
        catch (Exception ex)
        {
            // Isolate media forwarding faults from the RTP/callback threads.
            _logger.LogDebug(ex, "Media frame forwarding to the sender failed; dropping the frame.");
        }
    }

    /// <summary>
    /// Stops the forwarding loop and detaches frame subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _receiver.FrameReceived -= OnFrameReceived;
        _queue.Writer.TryComplete();
        _shutdownCts.Cancel();
        try
        {
            _pumpTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Media forwarding pump faulted during shutdown drain.");
        }
        _shutdownCts.Dispose();
    }
}
