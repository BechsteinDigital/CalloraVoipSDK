namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Shared time units and conversion for transport-wide congestion control
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1), used by the feedback builder,
/// interpreter, and delay correlator so the wire units stay defined in one place.
/// </summary>
internal static class TransportCcTime
{
    /// <summary>Reference-time unit in microseconds (the report reference time is in 64 ms steps).</summary>
    public const long ReferenceTimeUnitMicros = 64_000;

    /// <summary>Receive-delta unit in microseconds (per-packet deltas are in 250 µs steps).</summary>
    public const long DeltaUnitMicros = 250;

    private const long MicrosPerSecond = 1_000_000;

    /// <summary>
    /// Converts a monotonic tick count to microseconds without overflow, by splitting into whole
    /// seconds plus the sub-second remainder so the intermediate product stays bounded.
    /// </summary>
    public static long ToMicros(long ticks, long ticksPerSecond)
    {
        var seconds = ticks / ticksPerSecond;
        var remainder = ticks % ticksPerSecond;
        return seconds * MicrosPerSecond + remainder * MicrosPerSecond / ticksPerSecond;
    }
}
