using CalloraVoipSdk.InteropHarness.Metrics;

namespace CalloraVoipSdk.SoakTests.Metrics;

public sealed class ResourcePlateauAssertionsTests
{
    private static ResourceSample WithSockets(int index, int sockets) => new(
        SampleIndex: index, ManagedBytes: 0, PrivateMemoryBytes: 0, WorkingSetBytes: 0,
        ThreadCount: 10, HandleCount: 0, FileDescriptorCount: sockets + 5, SocketDescriptorCount: sockets);

    [Fact]
    public void WithinPlateau_FlatCounts_NotExceeded()
    {
        var samples = new[]
        {
            WithSockets(0, 8), WithSockets(1, 8), WithSockets(2, 9), WithSockets(3, 8), WithSockets(4, 8),
        };

        var r = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 0, "SocketDescriptorCount");

        Assert.False(r.Exceeded, r.Detail);
        Assert.False(r.Skipped);
    }

    [Fact]
    public void WithinPlateau_MonotonicGrowth_Exceeded()
    {
        var samples = new[]
        {
            WithSockets(0, 8), WithSockets(1, 12), WithSockets(2, 20), WithSockets(3, 30), WithSockets(4, 45),
        };

        var r = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 0, "SocketDescriptorCount");

        Assert.True(r.Exceeded, r.Detail);
        Assert.Contains("SocketDescriptorCount", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithinPlateau_GrowthWithinTolerance_NotExceeded()
    {
        // Baseline-Median 8, Ende-Median 10, Toleranz 2 ⇒ Deckel 10 ⇒ 10 > 10 ist false.
        var samples = new[]
        {
            WithSockets(0, 8), WithSockets(1, 8), WithSockets(2, 9), WithSockets(3, 10), WithSockets(4, 10),
        };

        var r = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 2, "SocketDescriptorCount");

        Assert.False(r.Exceeded, r.Detail);
    }

    [Fact]
    public void WithinPlateau_UnavailableMetric_IsSkipped()
    {
        // Sentinel -1 (z. B. FDs außerhalb Linux) ⇒ übersprungen, nicht als Leak gewertet.
        var samples = new[]
        {
            new ResourceSample(0, 0, 0, 0, 10, 0, FileDescriptorCount: -1, SocketDescriptorCount: -1),
            new ResourceSample(1, 0, 0, 0, 10, 0, -1, -1),
            new ResourceSample(2, 0, 0, 0, 10, 0, -1, -1),
        };

        var r = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.FileDescriptorCount, absoluteTolerance: 8, "FileDescriptorCount");

        Assert.True(r.Skipped);
        Assert.False(r.Exceeded);
    }
}
