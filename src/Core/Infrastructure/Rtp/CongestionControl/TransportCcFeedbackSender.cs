using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Receive-side transport-wide congestion-control feedback for one video stream
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01): records the transport-wide sequence number
/// (RFC 8285 header extension) and arrival time of each incoming packet, and on a fixed feedback interval
/// builds and sends a transport-cc RTCP report so the sender's congestion controller can estimate the path.
/// <para>
/// Timer-driven and decoupled from packet arrival, matching the reference stacks (libwebrtc's
/// <c>RemoteEstimatorProxy</c> periodic feedback and Pion's ticker-based sender): a packet-triggered send
/// would never flush the final arrivals once the stream pauses or ends, starving the remote estimator of tail
/// feedback. Recording runs on the RTP receive-loop thread; the periodic flush runs on the loop task — the two
/// synchronise on <see cref="_sync"/> (the arrival recorder is independently thread-safe).
/// </para>
/// Inactive unless the a=extmap for transport-cc was negotiated (the stream only constructs this when an
/// extension id is present), so nothing is sent on a leg that did not offer it.
/// </summary>
internal sealed class TransportCcFeedbackSender : IAsyncDisposable
{
    // A feedback batch spans at most one interval; a generous ring bounds the receive-side memory
    // and, on overflow (a very high packet rate), drops the oldest arrivals (counted, not silent).
    private const int RecorderCapacity = 1024;
    private const int FeedbacksPerSecond = 10; // ~100 ms between reports

    private readonly IRtcpPacketCodec _codec;
    private readonly byte _extensionId;
    private readonly uint _localSsrc;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendControl;
    private readonly Func<long> _timestamp;
    private readonly long _ticksPerSecond;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TransportCcArrivalRecorder _recorder = new(RecorderCapacity);
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;
    private readonly object _sync = new();

    // Guarded by _sync: written on the receive-loop thread (recording), read on the flush loop.
    private long _epoch;
    private bool _hasEpoch;
    private uint _remoteSsrc;

    // Flush-loop-thread only (single flusher): the monotonic feedback counter and the last-logged drop count.
    private byte _feedbackPacketCount;
    private long _lastReportedDrops;

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _disposed;

    public TransportCcFeedbackSender(
        IRtcpPacketCodec codec,
        byte extensionId,
        uint localSsrc,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendControl,
        Func<long> timestamp,
        long ticksPerSecond,
        ILogger logger,
        CancellationToken lifetime,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendControl);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);

        _codec = codec;
        _extensionId = extensionId;
        _localSsrc = localSsrc;
        _sendControl = sendControl;
        _timestamp = timestamp;
        _ticksPerSecond = ticksPerSecond;
        _interval = TimeSpan.FromSeconds(1.0 / FeedbacksPerSecond);
        _delay = delay ?? Task.Delay;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Starts the periodic feedback loop. Idempotent and safe after disposal (a no-op). The loop stops when the
    /// stream lifetime token cancels or the sender is disposed.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_loop is not null || _disposed != 0)
                return;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            // Offload to the thread pool (matching BundledRtcpReporter) so Start never runs the loop's
            // synchronous head on the caller's receive-loop thread.
            _loop = Task.Run(() => RunAsync(_loopCts.Token));
        }
    }

    /// <summary>
    /// Records one incoming RTP packet's transport-wide sequence number and arrival time. transport-cc numbers
    /// the transport, not a stream, so this observes every arriving packet (on a bundle, audio and video across
    /// all MIDs); a packet without the transport-cc header extension is ignored. Must be called on the single
    /// RTP receive-loop thread. The feedback itself is sent by the periodic flush loop, not here.
    /// </summary>
    public void OnRtpPacketReceived(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(
                packet.HeaderExtension, _extensionId, out var sequenceNumber))
            return;

        var now = _timestamp();
        lock (_sync)
        {
            if (!_hasEpoch)
            {
                _epoch = now;
                _hasEpoch = true;
            }
            _remoteSsrc = packet.Ssrc;
        }

        _recorder.Record(sequenceNumber, now); // the recorder is independently thread-safe
    }

    /// <summary>Test seam: runs one feedback flush synchronously, as the periodic loop would on a tick.</summary>
    internal void FlushForTest() => FlushFeedback();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _delay(_interval, ct).ConfigureAwait(false);
                FlushFeedback();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Transport-cc feedback loop stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transport-cc feedback loop failed unexpectedly.");
        }
    }

    // Builds and sends the feedback for the arrivals seen since the last flush. Runs on the loop thread; a
    // flush before any packet, or with an empty batch, sends nothing.
    private void FlushFeedback()
    {
        IReadOnlyList<TransportCcArrival> batch;
        uint remoteSsrc;
        long epoch;
        lock (_sync)
        {
            if (!_hasEpoch)
                return;

            batch = _recorder.Drain();

            // Surface receive-buffer overflow (arrivals overwritten at a pathological packet rate): the report
            // is then incomplete. Cumulative count — logged once per growth, not per report.
            var dropped = _recorder.DroppedCount;
            if (dropped > _lastReportedDrops)
            {
                _logger.LogDebug(
                    "Transport-cc arrival buffer overflow: {Dropped} arrival(s) dropped so far (capacity " +
                    "{Capacity}); the feedback report may be incomplete.", dropped, RecorderCapacity);
                _lastReportedDrops = dropped;
            }

            if (batch.Count == 0)
                return;

            remoteSsrc = _remoteSsrc;
            epoch = _epoch;
        }

        byte[] datagram;
        try
        {
            var feedback = TransportCcFeedbackBuilder.Build(
                batch, _localSsrc, remoteSsrc, _feedbackPacketCount, epoch, _ticksPerSecond);
            datagram = _codec.Encode([feedback]);
        }
        catch (ArgumentException ex)
        {
            // A batch that cannot be represented (e.g. a receive gap wider than the delta range, or a
            // sequence span beyond the unwrap window) is dropped rather than crashing the send path.
            _logger.LogDebug(ex, "Skipping a transport-cc feedback batch that could not be built.");
            return;
        }

        unchecked { _feedbackPacketCount++; } // advance only on a report actually built (loop thread only)
        _ = SendAsync(datagram);
    }

    private async Task SendAsync(byte[] datagram)
    {
        try
        {
            await _sendControl(datagram, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Transport-cc feedback send aborted by session teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send transport-cc feedback to the peer.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Task? loop;
        CancellationTokenSource? loopCts;
        lock (_sync)
        {
            loop = _loop;
            loopCts = _loopCts;
        }

        loopCts?.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false); // RunAsync swallows cancellation and returns normally
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Transport-cc feedback loop terminated with an error during dispose.");
            }
        }

        loopCts?.Dispose();
    }
}
