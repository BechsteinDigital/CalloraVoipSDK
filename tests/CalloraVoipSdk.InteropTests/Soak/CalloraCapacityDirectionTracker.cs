using System.Diagnostics;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed record CalloraCapacityFrameObservation(
    long Frames,
    DateTimeOffset? FirstFrameAtUtc,
    DateTimeOffset? LastFrameAtUtc,
    double MaximumGapMilliseconds,
    long GapsOver100Milliseconds,
    long GapsOver250Milliseconds,
    long GapsOver500Milliseconds,
    long GapsOver1000Milliseconds,
    double P50IntervalMilliseconds,
    double P95IntervalMilliseconds,
    double P99IntervalMilliseconds,
    double InterarrivalJitterMilliseconds);

internal sealed class CalloraCapacityDirectionTracker
{
    private const int ExactHistogramMaximumMilliseconds = 100;
    private const int Histogram250Milliseconds = 101;
    private const int Histogram500Milliseconds = 102;
    private const int Histogram1000Milliseconds = 103;
    private const int HistogramOverflow = 104;
    private const int HistogramLength = HistogramOverflow + 1;

    private readonly int[] _intervalHistogram = new int[HistogramLength];
    private readonly long _expectedIntervalTicks;

    private long _windowStartTicks;
    private long _windowEndTicks;
    private DateTimeOffset _windowStartAtUtc;
    private long _frames;
    private long _firstFrameTicks;
    private long _lastFrameTicks;
    private long _maximumInternalGapTicks;
    private long _gapsOver100Milliseconds;
    private long _gapsOver250Milliseconds;
    private long _gapsOver500Milliseconds;
    private long _gapsOver1000Milliseconds;
    private long _jitterTicks;
    private int _armed;

    public CalloraCapacityDirectionTracker(TimeSpan expectedInterval)
    {
        if (expectedInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedInterval));
        }

        _expectedIntervalTicks = ToStopwatchTicks(expectedInterval);
    }

    public void Arm(long windowStartTicks, long windowEndTicks, DateTimeOffset windowStartAtUtc)
    {
        if (windowEndTicks <= windowStartTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEndTicks));
        }

        Volatile.Write(ref _armed, 0);
        Array.Clear(_intervalHistogram);
        Interlocked.Exchange(ref _frames, 0);
        Interlocked.Exchange(ref _firstFrameTicks, 0);
        Interlocked.Exchange(ref _lastFrameTicks, 0);
        Interlocked.Exchange(ref _maximumInternalGapTicks, 0);
        Interlocked.Exchange(ref _gapsOver100Milliseconds, 0);
        Interlocked.Exchange(ref _gapsOver250Milliseconds, 0);
        Interlocked.Exchange(ref _gapsOver500Milliseconds, 0);
        Interlocked.Exchange(ref _gapsOver1000Milliseconds, 0);
        Interlocked.Exchange(ref _jitterTicks, 0);
        _windowStartAtUtc = windowStartAtUtc;
        Volatile.Write(ref _windowStartTicks, windowStartTicks);
        Volatile.Write(ref _windowEndTicks, windowEndTicks);
        Volatile.Write(ref _armed, 1);
    }

    public void Observe(long timestamp)
    {
        if (Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        var start = Volatile.Read(ref _windowStartTicks);
        var end = Volatile.Read(ref _windowEndTicks);
        if (timestamp < start || timestamp > end)
        {
            return;
        }

        Interlocked.CompareExchange(ref _firstFrameTicks, timestamp, 0);
        var previous = Interlocked.Exchange(ref _lastFrameTicks, timestamp);
        Interlocked.Increment(ref _frames);
        if (previous == 0 || timestamp <= previous)
        {
            return;
        }

        var gapTicks = timestamp - previous;
        UpdateMaximum(ref _maximumInternalGapTicks, gapTicks);
        CountGap(gapTicks);
        Interlocked.Increment(ref _intervalHistogram[HistogramIndex(gapTicks)]);

        var deviation = Math.Abs(gapTicks - _expectedIntervalTicks);
        var jitter = Volatile.Read(ref _jitterTicks);
        Interlocked.Exchange(ref _jitterTicks, jitter + ((deviation - jitter) / 16));
    }

    public CalloraCapacityFrameObservation Snapshot()
    {
        Volatile.Write(ref _armed, 0);
        var start = Volatile.Read(ref _windowStartTicks);
        var end = Volatile.Read(ref _windowEndTicks);
        var frames = Interlocked.Read(ref _frames);
        var first = Interlocked.Read(ref _firstFrameTicks);
        var last = Interlocked.Read(ref _lastFrameTicks);
        var maximumGap = Interlocked.Read(ref _maximumInternalGapTicks);
        var over100 = Interlocked.Read(ref _gapsOver100Milliseconds);
        var over250 = Interlocked.Read(ref _gapsOver250Milliseconds);
        var over500 = Interlocked.Read(ref _gapsOver500Milliseconds);
        var over1000 = Interlocked.Read(ref _gapsOver1000Milliseconds);

        if (frames == 0)
        {
            maximumGap = end - start;
            CountEdgeGap(maximumGap, ref over100, ref over250, ref over500, ref over1000);
        }
        else
        {
            var leadingGap = Math.Max(0, first - start);
            var trailingGap = Math.Max(0, end - last);
            maximumGap = Math.Max(maximumGap, Math.Max(leadingGap, trailingGap));
            CountEdgeGap(leadingGap, ref over100, ref over250, ref over500, ref over1000);
            CountEdgeGap(trailingGap, ref over100, ref over250, ref over500, ref over1000);
        }

        return new CalloraCapacityFrameObservation(
            frames,
            first == 0 ? null : ToUtc(first, start),
            last == 0 ? null : ToUtc(last, start),
            ToMilliseconds(maximumGap),
            over100,
            over250,
            over500,
            over1000,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            ToMilliseconds(Interlocked.Read(ref _jitterTicks)));
    }

    private void CountGap(long gapTicks)
    {
        if (gapTicks > MillisecondsToStopwatchTicks(100))
        {
            Interlocked.Increment(ref _gapsOver100Milliseconds);
        }
        if (gapTicks > MillisecondsToStopwatchTicks(250))
        {
            Interlocked.Increment(ref _gapsOver250Milliseconds);
        }
        if (gapTicks > MillisecondsToStopwatchTicks(500))
        {
            Interlocked.Increment(ref _gapsOver500Milliseconds);
        }
        if (gapTicks > MillisecondsToStopwatchTicks(1000))
        {
            Interlocked.Increment(ref _gapsOver1000Milliseconds);
        }
    }

    private static void CountEdgeGap(
        long gapTicks,
        ref long over100,
        ref long over250,
        ref long over500,
        ref long over1000)
    {
        if (gapTicks > MillisecondsToStopwatchTicks(100)) over100++;
        if (gapTicks > MillisecondsToStopwatchTicks(250)) over250++;
        if (gapTicks > MillisecondsToStopwatchTicks(500)) over500++;
        if (gapTicks > MillisecondsToStopwatchTicks(1000)) over1000++;
    }

    private double Percentile(double percentile)
    {
        var intervals = 0L;
        for (var index = 0; index < _intervalHistogram.Length; index++)
        {
            intervals += Volatile.Read(ref _intervalHistogram[index]);
        }

        if (intervals == 0)
        {
            return ToMilliseconds(
                Volatile.Read(ref _windowEndTicks) - Volatile.Read(ref _windowStartTicks));
        }

        var rank = (long)Math.Ceiling(intervals * percentile);
        var cumulative = 0L;
        for (var index = 0; index < _intervalHistogram.Length; index++)
        {
            cumulative += Volatile.Read(ref _intervalHistogram[index]);
            if (cumulative >= rank)
            {
                return index == HistogramOverflow
                    ? ToMilliseconds(
                        Volatile.Read(ref _windowEndTicks) - Volatile.Read(ref _windowStartTicks))
                    : HistogramUpperBoundMilliseconds(index);
            }
        }

        return double.PositiveInfinity;
    }

    private static int HistogramIndex(long gapTicks)
    {
        var milliseconds = ToMilliseconds(gapTicks);
        if (milliseconds <= ExactHistogramMaximumMilliseconds)
        {
            return Math.Clamp((int)Math.Ceiling(milliseconds), 0, ExactHistogramMaximumMilliseconds);
        }
        if (milliseconds <= 250) return Histogram250Milliseconds;
        if (milliseconds <= 500) return Histogram500Milliseconds;
        if (milliseconds <= 1000) return Histogram1000Milliseconds;
        return HistogramOverflow;
    }

    private static double HistogramUpperBoundMilliseconds(int index) =>
        index switch
        {
            <= ExactHistogramMaximumMilliseconds => index,
            Histogram250Milliseconds => 250,
            Histogram500Milliseconds => 500,
            Histogram1000Milliseconds => 1000,
            _ => double.PositiveInfinity,
        };

    private DateTimeOffset ToUtc(long timestamp, long start) =>
        _windowStartAtUtc + Stopwatch.GetElapsedTime(start, timestamp);

    private static long ToStopwatchTicks(TimeSpan duration) =>
        checked((long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency));

    private static long MillisecondsToStopwatchTicks(double milliseconds) =>
        checked((long)Math.Round(milliseconds / 1000d * Stopwatch.Frequency));

    private static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1000d / Stopwatch.Frequency;

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
