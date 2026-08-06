using System.Collections.Concurrent;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The DTLS egress pump (#191): a bounded single-writer send contract that preserves local record
/// order, propagates transport failures back into the handshake instead of swallowing them, and
/// drains a pending close_notify on teardown before the rest of the egress is cancelled.
/// </summary>
public sealed class DtlsEgressPumpTests
{
    [Fact]
    public async Task Enqueue_PreservesRecordOrder_WhenTheFirstSendBlocks()
    {
        var order = new ConcurrentQueue<byte>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        await using var pump = new DtlsEgressPump(
            async (datagram, _) =>
            {
                if (datagram[0] == 1)
                {
                    firstStarted.SetResult();
                    await release.Task;
                }
                else
                {
                    secondStarted = true;
                }

                order.Enqueue(datagram[0]);
            },
            NullLogger.Instance);

        pump.Enqueue(new byte[] { 1 });
        pump.Enqueue(new byte[] { 2 });

        // A single consumer holds record 1 exclusively while it blocks — record 2 must not be
        // sent ahead of it. The old fire-and-forget bridge would let record 2 finish first.
        await firstStarted.Task;
        await Task.Delay(50);
        Assert.False(secondStarted, "record 2 must not be sent while record 1 is in flight");

        release.SetResult();
        await pump.DrainAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 1, 2 }, order.ToArray());
    }

    [Fact]
    public async Task Enqueue_SurfacesAPersistentSendFailure_ToTheNextCaller()
    {
        await using var pump = new DtlsEgressPump(
            (_, _) => ValueTask.FromException(new SocketException(10054)), // connection reset
            NullLogger.Instance);

        pump.Enqueue(new byte[] { 1 });

        // Once the consumer has observed the failure and stopped, the next record surfaces it
        // synchronously so BouncyCastle aborts the handshake — the failure is not swallowed into
        // a warning as the old fire-and-forget bridge did.
        await pump.Completion;

        var ex = Assert.Throws<IOException>(() => pump.Enqueue(new byte[] { 2 }));
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public async Task Enqueue_AppliesBackpressure_WhenTheBoundedQueueIsFull()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pump = new DtlsEgressPump(
            async (_, _) =>
            {
                firstStarted.SetResult();
                await release.Task;
            },
            NullLogger.Instance,
            capacity: 1);

        // Record 1 is taken by the consumer and blocks; record 2 fills the single queue slot.
        pump.Enqueue(new byte[] { 1 });
        await firstStarted.Task;
        pump.Enqueue(new byte[] { 2 });

        // Record 3 cannot be admitted until a slot frees: Enqueue blocks instead of growing an
        // unbounded queue.
        var third = Task.Run(() => pump.Enqueue(new byte[] { 3 }));
        var raced = await Task.WhenAny(third, Task.Delay(200));
        Assert.NotSame(third, raced);

        release.SetResult();
        await pump.DrainAsync(TimeSpan.FromSeconds(2));
        await third;
    }

    [Fact]
    public async Task DrainAsync_FlushesAPendingRecord_WithinTheDeadline()
    {
        var sent = new ConcurrentQueue<byte>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pump = new DtlsEgressPump(
            async (datagram, ct) =>
            {
                await gate.Task.WaitAsync(ct);
                sent.Enqueue(datagram[0]);
            },
            NullLogger.Instance);

        pump.Enqueue(new byte[] { 42 }); // stand-in for a close_notify enqueued at teardown
        gate.SetResult();
        await pump.DrainAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 42 }, sent.ToArray()); // actually delivered, not cancelled away
    }

    [Fact]
    public async Task DrainAsync_ReturnsOnDeadline_AndDisposeLeavesNoRunningTask_WhenTheSocketIsDead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = new DtlsEgressPump(
            async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct); // send never completes — dead socket
            },
            NullLogger.Instance);

        pump.Enqueue(new byte[] { 1 });
        await started.Task;

        // The drain must return on its own deadline rather than hang on the dead send.
        var drain = pump.DrainAsync(TimeSpan.FromMilliseconds(200));
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(drain, finished);
        await drain;
        Assert.False(pump.Completion.IsCompleted); // drain did not cancel; the send is still parked

        await pump.DisposeAsync(); // cancels the parked send
        Assert.True(pump.Completion.IsCompleted); // no worker outlives dispose
    }
}
