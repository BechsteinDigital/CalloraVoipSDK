using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P2-9: an aborted hosted shutdown must stay resumable. <c>VoipClient.StopRuntimeAsync</c>
/// cleared its started flag BEFORE hanging up calls and unregistering lines, and both loops rethrow
/// <see cref="OperationCanceledException"/> — so a host stop that hit its shutdown timeout left the runtime
/// marked as stopped with calls still up and lines still registered, and the retry returned at the guard
/// without touching anything. The state is now claimed and then either completed or given back.
/// <para>
/// The teardown loops themselves need a registered line, which requires live SIP transport that cannot be
/// faked into <c>VoipClient.Lines</c> (same limitation as <c>VoipClientPublishLifecycleTests</c>), so the
/// resumability contract is pinned here on the extracted state machine and the facade's own no-op/restart
/// semantics are pinned in <see cref="VoipClientRuntimeLifecycleTests"/>.
/// </para>
/// </summary>
public sealed class RuntimeLifecycleStateTests
{
    [Fact]
    public void A_fresh_runtime_is_not_started_and_has_nothing_to_shut_down()
    {
        var state = new RuntimeLifecycleState();

        Assert.False(state.IsStarted);
        Assert.False(state.TryBeginShutdown());
    }

    [Fact]
    public void Only_the_first_start_and_the_first_shutdown_are_claimed()
    {
        var state = new RuntimeLifecycleState();

        Assert.True(state.TryStart());
        Assert.False(state.TryStart());
        Assert.True(state.IsStarted);

        Assert.True(state.TryBeginShutdown());
        // A second, concurrent shutdown does not double-drive the teardown, and the runtime no longer counts
        // as started while its teardown is in flight.
        Assert.False(state.TryBeginShutdown());
        Assert.False(state.IsStarted);
    }

    [Fact]
    public void An_aborted_shutdown_returns_the_runtime_to_started_and_can_be_retried()
    {
        var state = new RuntimeLifecycleState();
        state.TryStart();
        state.TryBeginShutdown();

        state.AbortShutdown();

        Assert.True(state.IsStarted);
        Assert.True(state.TryBeginShutdown());   // the retry resumes the teardown
        state.CompleteShutdown();
        Assert.False(state.IsStarted);
    }

    [Fact]
    public void A_completed_shutdown_stops_the_runtime_and_allows_a_restart()
    {
        var state = new RuntimeLifecycleState();
        state.TryStart();
        state.TryBeginShutdown();
        state.CompleteShutdown();

        Assert.False(state.IsStarted);
        Assert.False(state.TryBeginShutdown());  // nothing left to tear down
        Assert.True(state.TryStart());           // and the runtime can run again
    }

    [Fact]
    public void A_start_racing_an_in_flight_shutdown_does_not_take_the_state_from_it()
    {
        var state = new RuntimeLifecycleState();
        state.TryStart();
        state.TryBeginShutdown();

        Assert.False(state.TryStart());

        // The shutdown still owns the state and completes it.
        state.CompleteShutdown();
        Assert.False(state.IsStarted);
    }

    [Fact]
    public void Concurrent_shutdown_claims_elect_exactly_one_owner()
    {
        var state = new RuntimeLifecycleState();
        state.TryStart();

        var claims = 0;
        Parallel.For(0, 64, _ =>
        {
            if (state.TryBeginShutdown())
                Interlocked.Increment(ref claims);
        });

        Assert.Equal(1, Volatile.Read(ref claims));
    }
}
