using CalloraVoipSdk.InteropHarness.Media;

namespace CalloraVoipSdk.SoakTests.Media;

public sealed class RtpMediaLoopbackParallelStartTests
{
    [Fact]
    public async Task StartAsync_ManyInParallel_AllSucceedWithoutPortCollision()
    {
        // Startet viele Fixtures gleichzeitig — provoziert das FreeUdpPort-TOCTOU-Fenster.
        const int parallelism = 40;

        var started = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(_ => RtpMediaLoopback.StartAsync()));

        try
        {
            Assert.Equal(parallelism, started.Length);
            Assert.All(started, Assert.NotNull);
        }
        finally
        {
            await Task.WhenAll(started.Select(l => l.DisposeAsync().AsTask()));
        }
    }
}
