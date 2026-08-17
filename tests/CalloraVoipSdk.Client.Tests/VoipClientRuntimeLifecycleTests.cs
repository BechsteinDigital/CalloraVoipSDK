using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P2-9: the facade-level runtime lifecycle. A finished shutdown makes a second stop a no-op,
/// a cancelled stop is rejected before it claims the shutdown (so the runtime keeps its state), and the
/// runtime can be started again afterwards. The resumability of an ABORTED teardown is pinned on the
/// extracted state machine in <see cref="RuntimeLifecycleStateTests"/> — the teardown loops need a registered
/// line, which requires live SIP transport.
/// </summary>
public sealed class VoipClientRuntimeLifecycleTests
{
    private static VoipConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    [Fact]
    public async Task Stopping_a_started_runtime_completes_and_a_second_stop_is_a_no_op()
    {
        using var client = new VoipClient(TestConfiguration());

        await client.StartRuntimeAsync();
        await client.StopRuntimeAsync();
        await client.StopRuntimeAsync();
    }

    [Fact]
    public async Task Stopping_a_runtime_that_never_started_is_a_no_op()
    {
        using var client = new VoipClient(TestConfiguration());

        await client.StopRuntimeAsync();
    }

    [Fact]
    public async Task A_cancelled_stop_does_not_consume_the_started_state()
    {
        using var client = new VoipClient(TestConfiguration());
        await client.StartRuntimeAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StopRuntimeAsync(cancelled.Token));

        // Still started: the shutdown was never claimed, so a retry with a live token does the work.
        await client.StopRuntimeAsync();
    }

    [Fact]
    public async Task The_runtime_can_be_started_again_after_a_completed_shutdown()
    {
        using var client = new VoipClient(TestConfiguration());

        await client.StartRuntimeAsync();
        await client.StopRuntimeAsync();
        await client.StartRuntimeAsync();
        await client.StopRuntimeAsync();
    }

    [Fact]
    public async Task The_runtime_hooks_reject_a_disposed_client()
    {
        var client = new VoipClient(TestConfiguration());
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartRuntimeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StopRuntimeAsync());
    }
}
