using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>Deterministic scheduling checks for the RFC 8445 global ICE pacer.</summary>
public sealed class IceConnectivityCheckPacerTests
{
    [Fact]
    public async Task A_running_transaction_does_not_block_the_next_paced_check()
    {
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pacer = new IceConnectivityCheckPacer(
            NullLoggerFactory.Instance,
            TimeSpan.FromMilliseconds(50),
            (_, ct) => releasePace.Task.WaitAsync(ct));
        Assert.True(pacer.TryEnqueue(Work(IceConnectivityCheckKind.Ordinary, 200, ct => first.Task.WaitAsync(ct))));
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Ordinary,
            100,
            _ => { secondStarted.TrySetResult(); return Task.FromResult(true); })));

        pacer.Start();
        releasePace.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(first.Task.IsCompleted);
        first.TrySetResult(false);
    }

    [Fact]
    public async Task Triggered_fifo_preempts_higher_priority_ordinary_work()
    {
        var order = new List<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Enqueuing self-starts the drain, whose FIRST dequeue is not gated by the pacing delay (the delay runs
        // AFTER a dispatch). So a plain "enqueue ordinary, enqueue triggered" races: the drain can dispatch the
        // first-enqueued ordinary before triggered is even enqueued. To test preemption deterministically we park
        // the drain in the pacing gate with a primer check, THEN enqueue both real checks while it is parked, so
        // the decisive dequeue provably sees both — and must still pick the triggered one first (RFC 8445 §7.3.1.4).
        var primerDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pacer = new IceConnectivityCheckPacer(
            NullLoggerFactory.Instance,
            TimeSpan.Zero,
            (_, ct) => releasePace.Task.WaitAsync(ct));

        // Primer: consumes the first (ungated) dequeue, after which the drain parks in the pacing gate below.
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Ordinary,
            500,
            _ => { primerDispatched.TrySetResult(); return Task.FromResult(true); })));
        await primerDispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Drain is now parked awaiting releasePace. Enqueue both real checks before it can dequeue again.
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Ordinary,
            999,
            _ => { lock (order) order.Add("ordinary"); completed.TrySetResult(); return Task.FromResult(true); })));
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Triggered,
            1,
            _ => { lock (order) order.Add("triggered"); return Task.FromResult(true); })));

        releasePace.TrySetResult(); // resume the drain: its next dequeue sees both queued → triggered wins
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        lock (order)
            Assert.Equal(new[] { "triggered", "ordinary" }, order);
    }

    [Fact]
    public async Task Enqueued_work_dispatches_without_an_explicit_start()
    {
        // A triggered check learned off the receive loop (RFC 8445 §7.3.1.4) must run even when the owning
        // checklist has not called Start() yet: enqueuing self-starts the drain loop.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pacer = new IceConnectivityCheckPacer(
            NullLoggerFactory.Instance,
            TimeSpan.Zero,
            (_, _) => Task.CompletedTask);

        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Triggered,
            1,
            _ => { ran.TrySetResult(); return Task.FromResult(true); })));

        // Deliberately no pacer.Start() here.
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static IceConnectivityCheckWork Work(
        IceConnectivityCheckKind kind,
        long priority,
        Func<CancellationToken, Task<bool>> execute) => new()
    {
        Kind = kind,
        Priority = priority,
        Execute = execute,
        Complete = _ => { },
    };
}
