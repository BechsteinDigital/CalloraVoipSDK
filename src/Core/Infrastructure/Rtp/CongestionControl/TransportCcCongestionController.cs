using CalloraVoipSdk.Core.Application.Media;
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
/// fraction) into the delay-trend and loss estimators. It then feeds the resulting signal + loss into
/// the injected <see cref="CongestionBitrateController"/> to produce a ready-to-use
/// <see cref="RecommendedBitrateBps"/> and a coarse <see cref="Quality"/>; <see cref="Signal"/>,
/// <see cref="DelayTrendMicros"/> and <see cref="LossRatio"/> remain available as the raw state.
/// Orchestration over injected pieces — the AIMD policy itself lives in the bitrate controller.
/// </summary>
internal sealed class TransportCcCongestionController
{
    // Quality bands over the smoothed loss ratio; the delay signal escalates to Poor on its own.
    private const double PoorLossRatio = 0.1;  // ≥10% loss → congested
    private const double FairLossRatio = 0.02; // ≥2% loss → mild stress

    private readonly byte _extensionId;
    private readonly IRtcpPacketCodec _codec;
    private readonly TransportCcSendHistory _sendHistory;
    private readonly TransportCcDelayTrendEstimator _delayTrend;
    private readonly TransportCcLossEstimator _loss;
    private readonly CongestionBitrateController _bitrate;
    private readonly Func<long> _timestamp;
    private readonly long _ticksPerSecond;
    private readonly ILogger _logger;

    public TransportCcCongestionController(
        byte extensionId,
        IRtcpPacketCodec codec,
        TransportCcSendHistory sendHistory,
        TransportCcDelayTrendEstimator delayTrend,
        TransportCcLossEstimator loss,
        CongestionBitrateController bitrate,
        Func<long> timestamp,
        long ticksPerSecond,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendHistory);
        ArgumentNullException.ThrowIfNull(delayTrend);
        ArgumentNullException.ThrowIfNull(loss);
        ArgumentNullException.ThrowIfNull(bitrate);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);

        _extensionId = extensionId;
        _codec = codec;
        _sendHistory = sendHistory;
        _delayTrend = delayTrend;
        _loss = loss;
        _bitrate = bitrate;
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
    /// The ready-to-use recommended outbound video bitrate in bits per second. The application sets its
    /// encoder to this value; the SDK never encodes (transport-only). Updated on every feedback report.
    /// </summary>
    public long RecommendedBitrateBps => _bitrate.TargetBitrateBps;

    /// <summary>Coarse, ready-to-use network-quality indicator derived from the delay signal and loss.</summary>
    public NetworkQuality Quality =>
        _delayTrend.Signal == CongestionSignal.Overusing || _loss.LossRatio >= PoorLossRatio
            ? NetworkQuality.Poor
            : _loss.LossRatio >= FairLossRatio
                ? NetworkQuality.Fair
                : NetworkQuality.Good;

    /// <summary>
    /// Raised after a feedback report when <see cref="RecommendedBitrateBps"/> changed. Fires on the
    /// RTP control thread — handlers must be fast and must not block; exceptions are caught and logged.
    /// </summary>
    public event Action<long>? RecommendedBitrateChanged;

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

            // Fold the fresh signal + loss into the AIMD rate policy; surface a change once.
            var previousBitrate = _bitrate.TargetBitrateBps;
            _bitrate.Update(_delayTrend.Signal, _loss.LossRatio);
            var updatedBitrate = _bitrate.TargetBitrateBps;
            if (updatedBitrate != previousBitrate)
                RaiseRecommendedBitrateChanged(updatedBitrate);
        }
    }

    private void RaiseRecommendedBitrateChanged(long bitrateBps)
    {
        try
        {
            RecommendedBitrateChanged?.Invoke(bitrateBps);
        }
        catch (Exception ex)
        {
            // Isolate a subscriber's fault from the RTP control thread.
            _logger.LogDebug(ex, "Recommended-bitrate subscriber threw; continuing.");
        }
    }
}
