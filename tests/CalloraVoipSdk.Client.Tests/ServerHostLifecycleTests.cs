using System.Net;
using CalloraVoipSdk.Hosting;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P3-12: the STUN/TURN hosting facades need a real lifecycle. Both committed their started flag
/// BEFORE running the server's start, so a start that threw left the host marked as started forever: every
/// retry was a silent no-op and the host reported itself as serving while nothing listened. Starting after
/// disposal was silently swallowed for the same reason. The shared lifecycle commits only a start that
/// returned, and refuses to start a disposed host.
/// </summary>
public sealed class ServerHostLifecycleTests
{
    [Fact]
    public void A_failing_start_is_not_committed_and_can_be_retried()
    {
        var lifecycle = new ServerHostLifecycle();
        var attempts = 0;

        Assert.Throws<IOException>(() => lifecycle.Start(
            () =>
            {
                attempts++;
                throw new IOException("bind-boom");
            },
            owner: this));

        Assert.False(lifecycle.IsStarted);

        // The retry actually runs the start again instead of returning as an already-started host.
        lifecycle.Start(() => attempts++, owner: this);

        Assert.Equal(2, attempts);
        Assert.True(lifecycle.IsStarted);
    }

    [Fact]
    public void Only_the_first_start_runs()
    {
        var lifecycle = new ServerHostLifecycle();
        var starts = 0;

        lifecycle.Start(() => starts++, owner: this);
        lifecycle.Start(() => starts++, owner: this);

        Assert.Equal(1, starts);
    }

    [Fact]
    public void Starting_a_disposed_host_is_refused()
    {
        var lifecycle = new ServerHostLifecycle();
        Assert.True(lifecycle.TryBeginDispose());

        var started = false;
        Assert.Throws<ObjectDisposedException>(() => lifecycle.Start(() => started = true, owner: this));
        Assert.False(started);
    }

    [Fact]
    public void Disposal_is_claimed_exactly_once()
    {
        var lifecycle = new ServerHostLifecycle();

        Assert.True(lifecycle.TryBeginDispose());
        Assert.False(lifecycle.TryBeginDispose());
    }

    [Fact]
    public void Concurrent_starts_run_the_server_start_exactly_once()
    {
        var lifecycle = new ServerHostLifecycle();
        var starts = 0;

        Parallel.For(0, 64, _ => lifecycle.Start(() => Interlocked.Increment(ref starts), owner: this));

        Assert.Equal(1, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task A_disposed_stun_server_host_refuses_to_start()
    {
        var host = new StunServerHost(new StunServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        host.Start();
        host.Start();   // idempotent while alive
        await host.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(host.Start);
        await host.DisposeAsync();   // dispose stays idempotent
    }

    [Fact]
    public async Task A_disposed_turn_server_host_refuses_to_start()
    {
        var host = new TurnServerHost(new TurnServerHostConfiguration
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            RequireAuthentication = false,
        });

        host.Start();
        host.Start();
        await host.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(host.Start);
        await host.DisposeAsync();
    }
}
