using CalloraVoipSdk.Core.Infrastructure.WebRtc;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The drain gate behind the WebRTC Send-vs-Dispose fix (HARD-C6): a drain refuses new entries and
/// completes only once every in-flight entry has exited, so a disposer can safely tear down a shared
/// resource the moment the drain task completes.
/// </summary>
public sealed class SendDrainGateTests
{
    [Fact]
    public async Task Drain_waits_for_the_in_flight_entry_then_completes()
    {
        var gate = new SendDrainGate();
        Assert.True(gate.TryEnter());

        var drain = gate.BeginDrainAsync();
        Assert.False(drain.IsCompleted);   // one entry in flight → the drain must wait

        gate.Exit();
        await drain;                        // the last exit releases the drain
    }

    [Fact]
    public void Drain_completes_immediately_when_nothing_is_in_flight()
        => Assert.True(new SendDrainGate().BeginDrainAsync().IsCompleted);

    [Fact]
    public void TryEnter_is_refused_after_the_drain_begins()
    {
        var gate = new SendDrainGate();
        _ = gate.BeginDrainAsync();
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Drain_releases_only_on_the_last_of_several_exits()
    {
        var gate = new SendDrainGate();
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var drain = gate.BeginDrainAsync();
        gate.Exit();
        Assert.False(drain.IsCompleted);   // one entry still in flight
        gate.Exit();
        await drain;                        // now drained
    }

    [Fact]
    public async Task BeginDrainAsync_is_idempotent()
    {
        var gate = new SendDrainGate();
        Assert.True(gate.TryEnter());

        var first = gate.BeginDrainAsync();
        var second = gate.BeginDrainAsync();
        Assert.False(first.IsCompleted);

        gate.Exit();
        await Task.WhenAll(first, second);
    }
}
