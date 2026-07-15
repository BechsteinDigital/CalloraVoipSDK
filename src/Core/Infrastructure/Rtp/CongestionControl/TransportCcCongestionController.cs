using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Sender-side congestion controller for one video stream: the counterpart to
/// <see cref="TransportCcFeedbackSender"/>. It records when each stamped outgoing packet went on the
/// wire and, when a transport-cc feedback report arrives from the peer, reconstructs the arrivals,
/// correlates them with those send times into delay gradients, and folds them (plus the loss
/// fraction) into the delay-trend and loss estimators. The resulting <see cref="Signal"/>,
/// <see cref="DelayTrendMicros"/>, and <see cref="LossRatio"/> are the congestion state a rate-control
/// policy or the application reads. Pure orchestration over injected pieces — it decides no bitrate.
/// </summary>
internal sealed class TransportCcCongestionController
{
    private readonly byte _extensionId;
    private readonly IRtcpPacketCodec _codec;
    private readonly TransportCcSendHistory _sendHistory;
    private readonly TransportCcDelayTrendEstimator _delayTrend;
    private readonly TransportCcLossEstimator _loss;
    private readonly Func<long> _timestamp;
    private readonly long _ticksPerSecond;
    private readonly ILogger _logger;

    public TransportCcCongestionController(
        byte extensionId,
        IRtcpPacketCodec codec,
        TransportCcSendHistory sendHistory,
        TransportCcDelayTrendEstimator delayTrend,
        TransportCcLossEstimator loss,
        Func<long> timestamp,
        long ticksPerSecond,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendHistory);
        ArgumentNullException.ThrowIfNull(delayTrend);
        ArgumentNullException.ThrowIfNull(loss);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);

        _extensionId = extensionId;
        _codec = codec;
        _sendHistory = sendHistory;
        _delayTrend = delayTrend;
        _loss = loss;
        _timestamp = timestamp;
        _ticksPerSecond = ticksPerSecond;
        _logger = logger;
    }

    /// <summary>The current coarse congestion signal from the delay trend.</summary>
    public CongestionSignal Signal => _delayTrend.Signal;

    /// <summary>The current smoothed one-way delay trend in microseconds (positive = delay rising).</summary>
    public double DelayTrendMicros => _delayTrend.TrendMicros;

    /// <summary>The current smoothed packet-loss ratio in [0, 1].</summary>
    public double LossRatio => _loss.LossRatio;

    /// <summary>
    /// Records that a stamped outgoing packet went on the wire. A packet without the transport-cc
    /// header extension is ignored. Called on the send path.
    /// </summary>
    public void OnPacketSent(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(
                packet.HeaderExtension, _extensionId, out var sequenceNumber))
            _sendHistory.Record(sequenceNumber, _timestamp());
    }

    /// <summary>
    /// Processes an inbound RTCP datagram (already SRTCP-unprotected): each transport-cc report it
    /// contains updates the delay-trend and loss estimators. A malformed datagram is dropped —
    /// feedback must never break the receive path.
    /// </summary>
    public void OnControlDatagram(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        IReadOnlyList<RtcpPacket> packets;
        try
        {
            packets = _codec.Decode(datagram);
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Dropping malformed inbound RTCP datagram for transport-cc.");
            return;
        }

        foreach (var packet in packets)
        {
            if (packet is not RtcpTransportFeedback feedback)
                continue;

            var results = TransportCcFeedbackInterpreter.Interpret(feedback);
            var samples = TransportCcFeedbackCorrelator.Correlate(results, _sendHistory, _ticksPerSecond);
            _delayTrend.Observe(samples);
            _loss.Observe(results);
        }
    }
}
