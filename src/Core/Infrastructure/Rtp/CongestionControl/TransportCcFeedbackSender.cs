using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Receive-side transport-wide congestion-control feedback for one video stream
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01): records the transport-wide sequence number
/// (RFC 8285 header extension) and arrival time of each incoming packet, and roughly every feedback
/// interval builds and sends a transport-cc RTCP report so the sender's congestion controller can
/// estimate the path. Packet-triggered rather than timer-driven — sending is checked as packets
/// arrive on the RTP receive-loop thread, so it needs no background timer and stays deterministic.
/// Inactive unless the a=extmap for transport-cc was negotiated (the stream only constructs this
/// when an extension id is present), so nothing is sent on a leg that did not offer it.
/// </summary>
internal sealed class TransportCcFeedbackSender
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
    private readonly long _intervalTicks;
    private readonly TransportCcArrivalRecorder _recorder = new(RecorderCapacity);
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    // Receive-loop-thread state (single consumer — same thread as VideoRtpStream.OnPacketReceived),
    // so it needs no synchronisation; the recorder itself is independently thread-safe.
    private long _epoch;
    private bool _hasEpoch;
    private long _lastSendTimestamp;
    private uint _remoteSsrc;
    private byte _feedbackPacketCount;
    private long _lastReportedDrops;

    public TransportCcFeedbackSender(
        IRtcpPacketCodec codec,
        byte extensionId,
        uint localSsrc,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendControl,
        Func<long> timestamp,
        long ticksPerSecond,
        ILogger logger,
        CancellationToken lifetime)
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
        _intervalTicks = ticksPerSecond / FeedbacksPerSecond;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Records one incoming video packet's transport-wide sequence number and arrival time, and sends
    /// a feedback report when a feedback interval has elapsed. A packet without the transport-cc
    /// header extension is ignored. Must be called on the single RTP receive-loop thread.
    /// </summary>
    public void OnVideoPacketReceived(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(
                packet.HeaderExtension, _extensionId, out var sequenceNumber))
            return;

        var now = _timestamp();
        if (!_hasEpoch)
        {
            _epoch = now;
            _lastSendTimestamp = now;
            _hasEpoch = true;
        }

        _remoteSsrc = packet.Ssrc;
        _recorder.Record(sequenceNumber, now);

        if (now - _lastSendTimestamp >= _intervalTicks)
        {
            _lastSendTimestamp = now;
            SendFeedback();
        }
    }

    private void SendFeedback()
    {
        var batch = _recorder.Drain();

        // Surface receive-buffer overflow (arrivals overwritten at a pathological packet rate): the
        // report is then incomplete. Cumulative count — logged once per growth, not per report.
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

        byte[] datagram;
        try
        {
            var feedback = TransportCcFeedbackBuilder.Build(
                batch, _localSsrc, _remoteSsrc, _feedbackPacketCount, _epoch, _ticksPerSecond);
            datagram = _codec.Encode([feedback]);
        }
        catch (ArgumentException ex)
        {
            // A batch that cannot be represented (e.g. a receive gap wider than the delta range, or a
            // sequence span beyond the unwrap window) is dropped rather than crashing the receive path.
            _logger.LogDebug(ex, "Skipping a transport-cc feedback batch that could not be built.");
            return;
        }

        unchecked { _feedbackPacketCount++; }
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
}
