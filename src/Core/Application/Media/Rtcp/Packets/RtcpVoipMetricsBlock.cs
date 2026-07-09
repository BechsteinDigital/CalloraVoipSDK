namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP-XR VoIP Metrics report block (block type 7) — RFC 3611 §4.7. Carries call-quality
/// metrics for one synchronization source. Rates and densities are 8-bit fixed-point fractions
/// (value / 256); durations and delays are milliseconds; MOS scores are stored as the score
/// multiplied by 10 (a value of 127 means "unavailable").
/// </summary>
internal sealed class RtcpVoipMetricsBlock
{
    /// <summary>SSRC of the source these metrics describe.</summary>
    public required uint SourceSsrc { get; init; }

    /// <summary>Fraction of packets lost since the start of reception (value / 256).</summary>
    public byte LossRate { get; init; }

    /// <summary>Fraction of packets discarded (jitter/late) since reception start (value / 256).</summary>
    public byte DiscardRate { get; init; }

    /// <summary>Fraction of lost/discarded packets within bursts (value / 256).</summary>
    public byte BurstDensity { get; init; }

    /// <summary>Fraction of lost/discarded packets within gaps (value / 256).</summary>
    public byte GapDensity { get; init; }

    /// <summary>Mean duration of burst periods, in milliseconds.</summary>
    public ushort BurstDurationMs { get; init; }

    /// <summary>Mean duration of gap periods, in milliseconds.</summary>
    public ushort GapDurationMs { get; init; }

    /// <summary>Most recently estimated network round-trip time, in milliseconds.</summary>
    public ushort RoundTripDelayMs { get; init; }

    /// <summary>Most recently estimated end-system delay, in milliseconds.</summary>
    public ushort EndSystemDelayMs { get; init; }

    /// <summary>Voice-quality R factor (0–100), or 127 when unavailable.</summary>
    public byte RFactor { get; init; }

    /// <summary>External (network segment) R factor, or 127 when unavailable.</summary>
    public byte ExternalRFactor { get; init; }

    /// <summary>Listening-quality MOS × 10 (10–50), or 127 when unavailable.</summary>
    public byte MosLq { get; init; }

    /// <summary>Conversational-quality MOS × 10 (10–50), or 127 when unavailable.</summary>
    public byte MosCq { get; init; }

    /// <summary>Nominal jitter-buffer delay, in milliseconds.</summary>
    public ushort JitterBufferNominalMs { get; init; }

    /// <summary>Maximum jitter-buffer delay, in milliseconds.</summary>
    public ushort JitterBufferMaximumMs { get; init; }

    /// <summary>Absolute maximum jitter-buffer delay the implementation can reach, in milliseconds.</summary>
    public ushort JitterBufferAbsoluteMaxMs { get; init; }
}
