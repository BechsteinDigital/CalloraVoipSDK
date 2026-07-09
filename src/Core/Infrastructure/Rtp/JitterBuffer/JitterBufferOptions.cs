namespace CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;

/// <summary>
/// Tuning parameters for the adaptive jitter buffer.
/// </summary>
internal sealed class JitterBufferOptions
{
    /// <summary>Minimum playout delay in milliseconds (default 20 ms).</summary>
    public int MinDelayMs { get; init; } = 20;

    /// <summary>Maximum playout delay in milliseconds (default 300 ms).</summary>
    public int MaxDelayMs { get; init; } = 300;

    /// <summary>Initial playout delay in milliseconds (default 60 ms).</summary>
    public int InitialDelayMs { get; init; } = 60;

    /// <summary>Maximum number of packets held simultaneously (default 50).</summary>
    public int Capacity { get; init; } = 50;

    /// <summary>RTP clock rate in Hz used to convert timestamps to milliseconds (default 8000 for PCMU/PCMA).</summary>
    public int ClockRate { get; init; } = 8000;

    /// <summary>
    /// Initial RTT hint in milliseconds used by adaptive delay control until the first real
    /// RTCP RTT sample arrives — that first sample replaces this seed rather than smoothing
    /// from it, so convergence stays fast. Seeded to 100 ms (a conservative WAN round trip) so
    /// the delay floor has an RTT budget during the first RTCP interval instead of zero, which
    /// avoids early-call underruns before the first Sender/Receiver Report.
    /// </summary>
    public double InitialRoundTripTimeMs { get; init; } = 100;

    /// <summary>
    /// EWMA smoothing factor for RTT updates in the range [0, 1].
    /// Higher values react faster to new RTT samples.
    /// </summary>
    public double RoundTripTimeSmoothingFactor { get; init; } = 0.2;

    /// <summary>
    /// Converts RTT into additional playout-delay budget.
    /// Example: 0.10 means 100 ms RTT contributes 10 ms delay floor.
    /// </summary>
    public double RoundTripTimeDelayWeight { get; init; } = 0.10;

    /// <summary>
    /// Converts estimated inter-arrival jitter into additional playout-delay budget.
    /// Example: 1.25 means 8 ms jitter contributes 10 ms delay floor.
    /// </summary>
    public double JitterDelayWeight { get; init; } = 1.25;

    /// <summary>
    /// Hard upper bound for accepted RTT hints in milliseconds.
    /// Prevents pathological or malformed inputs from exploding delay adaptation.
    /// </summary>
    public double MaxRoundTripTimeMs { get; init; } = 2000;
}
