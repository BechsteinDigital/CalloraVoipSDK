using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Dispose-safety gate for <see cref="VoipClient"/> (HARD-C4). Disposal is claimed atomically via
/// <c>Interlocked.Exchange</c>, so the teardown runs at most once even when several threads race on
/// <see cref="VoipClient.Dispose"/>, and guarded operations fail closed afterwards.
/// </summary>
public sealed class VoipClientDisposeTests
{
    private static VoipConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    [Fact]
    public void Dispose_is_idempotent()
    {
        var client = new VoipClient(TestConfiguration());

        client.Dispose();
        var second = Record.Exception(() => client.Dispose());

        Assert.Null(second);
    }

    [Fact]
    public void Guarded_operation_throws_after_dispose()
    {
        var client = new VoipClient(TestConfiguration());
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.GetAvailableInputAudioDevices());
    }

    [Fact]
    public async Task Concurrent_dispose_does_not_fault_and_ends_disposed()
    {
        var client = new VoipClient(TestConfiguration());

        // Release many threads into Dispose() at once. With the atomic guard exactly one runs the
        // teardown and the rest return immediately, so no caller observes a double-teardown fault.
        using var gate = new ManualResetEventSlim(false);
        var racers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            client.Dispose();
        })).ToArray();

        gate.Set();
        var faults = await Task.WhenAll(racers.Select(async t =>
        {
            try { await t; return (Exception?)null; }
            catch (Exception ex) { return ex; }
        }));

        Assert.All(faults, Assert.Null);
        Assert.Throws<ObjectDisposedException>(() => client.GetAvailableInputAudioDevices());
    }
}
