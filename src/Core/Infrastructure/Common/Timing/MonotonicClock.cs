using System.Diagnostics;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Timing;

/// <summary>
/// A monotonically non-decreasing clock for measuring elapsed time and scheduling media playout, immune to
/// wall-clock adjustments (NTP steps, manual changes, DST, leap-second smears). Values are synthetic instants
/// anchored at an arbitrary process epoch — only <em>differences</em> between them are meaningful; a value must
/// not be interpreted as an absolute date/time. Backed by <see cref="Stopwatch"/> (a monotonic timer), so
/// successive reads never move backwards the way <see cref="DateTimeOffset.UtcNow"/> can when the system clock
/// is stepped — which for a jitter buffer would otherwise stall playout (a backward step) or dump the queue as
/// "late" (a forward step).
/// </summary>
internal static class MonotonicClock
{
    // Anchored once at first use. The absolute value is irrelevant since only deltas are ever consumed.
    private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// A monotonically non-decreasing instant, immune to wall-clock jumps. Suitable as the shared arrival and
    /// playout clock for a jitter buffer, where both reads must come from the same jump-free source.
    /// </summary>
    public static DateTimeOffset Now => DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(OriginTimestamp);
}
