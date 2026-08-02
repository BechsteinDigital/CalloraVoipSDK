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

        await using var pacer = new IceConnectivityCheckPacer(
            NullLoggerFactory.Instance,
            TimeSpan.Zero,
            (_, _) => Task.CompletedTask);
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Ordinary,
            999,
            _ => { lock (order) order.Add("ordinary"); completed.TrySetResult(); return Task.FromResult(true); })));
        Assert.True(pacer.TryEnqueue(Work(
            IceConnectivityCheckKind.Triggered,
            1,
            _ => { lock (order) order.Add("triggered"); return Task.FromResult(true); })));

        pacer.Start();
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
