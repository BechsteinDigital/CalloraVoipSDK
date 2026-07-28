using System.Diagnostics;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Soak;

/// <summary>Validiert die randinklusive, allokationsarme Frame-Zeitmessung.</summary>
public sealed class CalloraCapacityDirectionTrackerTests
{
    /// <summary>Regelmäßige 20-ms-Frames ergeben vollständige Abdeckung ohne lange Lücke.</summary>
    [Fact]
    public void Snapshot_RegularFrames_ReportsExpectedCadence()
    {
        var tracker = NewTracker();
        var start = Stopwatch.GetTimestamp() + Ticks(100);
        var end = start + Ticks(200);
        tracker.Arm(start, end, DateTimeOffset.UnixEpoch);

        for (var offset = 20; offset <= 200; offset += 20)
        {
            tracker.Observe(start + Ticks(offset));
        }

        var observation = tracker.Snapshot();

        Assert.Equal(10, observation.Frames);
        Assert.InRange(observation.MaximumGapMilliseconds, 19.9, 20.1);
        Assert.InRange(observation.P99IntervalMilliseconds, 19, 21);
        Assert.Equal(0, observation.GapsOver100Milliseconds);
        Assert.InRange(observation.InterarrivalJitterMilliseconds, 0, 0.1);
    }

    /// <summary>Fensteranfang und -ende zählen als Stille, nicht nur Abstände zwischen Frames.</summary>
    [Fact]
    public void Snapshot_SparseFrames_IncludesWindowEdgesInGapDiagnostics()
    {
        var tracker = NewTracker();
        var start = Stopwatch.GetTimestamp() + Ticks(100);
        var end = start + Ticks(1000);
        tracker.Arm(start, end, DateTimeOffset.UnixEpoch);
        tracker.Observe(start + Ticks(100));
        tracker.Observe(start + Ticks(800));

        var observation = tracker.Snapshot();

        Assert.InRange(observation.MaximumGapMilliseconds, 699.9, 700.1);
        Assert.Equal(2, observation.GapsOver100Milliseconds);
        Assert.Equal(1, observation.GapsOver250Milliseconds);
        Assert.Equal(1, observation.GapsOver500Milliseconds);
        Assert.Equal(0, observation.GapsOver1000Milliseconds);
    }

    /// <summary>Frames außerhalb des vorab festgelegten Fensters verfälschen die Messung nicht.</summary>
    [Fact]
    public void Snapshot_FramesOutsideWindow_AreIgnored()
    {
        var tracker = NewTracker();
        var start = Stopwatch.GetTimestamp() + Ticks(100);
        var end = start + Ticks(500);
        tracker.Arm(start, end, DateTimeOffset.UnixEpoch);
        tracker.Observe(start - Ticks(1));
        tracker.Observe(end + Ticks(1));

        var observation = tracker.Snapshot();

        Assert.Equal(0, observation.Frames);
        Assert.InRange(observation.MaximumGapMilliseconds, 499.9, 500.1);
        Assert.Equal(1, observation.GapsOver250Milliseconds);
    }

    private static CalloraCapacityDirectionTracker NewTracker() =>
        new(TimeSpan.FromMilliseconds(20));

    private static long Ticks(double milliseconds) =>
        (long)Math.Round(milliseconds / 1000d * Stopwatch.Frequency);
}
