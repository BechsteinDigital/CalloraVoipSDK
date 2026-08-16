using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Disposing a media link joins its forwarding pump, and the pump is sitting inside a sender the SDK does not
/// own. A sender that ignores its cancellation token — or simply blocks in its own I/O — must not be able to
/// hold the whole shutdown: the join is bounded and then abandoned (#165 P2-9). A sender that does stop is
/// still awaited, so an ordinary teardown keeps draining what it always drained.
/// </summary>
public sealed class MediaConnectionDisposeTests
{
    [Fact]
    public async Task Dispose_returns_even_when_the_sender_ignores_cancellation()
    {
        var receiver = new FakeReceiver();
        var stuck = new StuckSender();
        var connection = new MediaConnection(receiver, stuck, queueCapacity: 8, NullLogger.Instance);

        receiver.Raise(new MediaFrame(new byte[160], 0, 160));
        Assert.True(await stuck.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var clock = Stopwatch.StartNew();
        var dispose = Task.Run(connection.Dispose);
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(20)));
        clock.Stop();

        try
        {
            Assert.Same(dispose, finished); // before the fix this waited on the sender forever
            await dispose;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"disposal took {clock.Elapsed}");
        }
        finally
        {
            stuck.Release(); // let the abandoned pump finish so the test process stays clean
        }
    }

    [Fact]
    public async Task Dispose_still_drains_a_sender_that_honours_cancellation()
    {
        var receiver = new FakeReceiver();
        var polite = new CancellationHonouringSender();
        var connection = new MediaConnection(receiver, polite, queueCapacity: 8, NullLogger.Instance);

        receiver.Raise(new MediaFrame(new byte[160], 0, 160));
        Assert.True(await polite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var clock = Stopwatch.StartNew();
        await Task.Run(connection.Dispose);
        clock.Stop();

        Assert.True(polite.Cancelled, "the sender should have observed cancellation");
        // It stopped on its own, so the join returned immediately rather than sitting out the deadline.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"disposal waited {clock.Elapsed} on a co-operative sender");
    }

    private sealed class FakeReceiver : IMediaReceiver
    {
        public event EventHandler<MediaFrameReceivedEventArgs>? FrameReceived;

        public void Raise(MediaFrame frame) => FrameReceived?.Invoke(this, new MediaFrameReceivedEventArgs(frame));

        public void AttachToCall(ICall call) { }
        public void Detach() { }
        public void Dispose() { }
    }

    // Never completes and never looks at the token — the case the deadline exists for.
    private sealed class StuckSender : IMediaSender
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(MediaFrame frame, CancellationToken ct = default)
        {
            Entered.TrySetResult(true);
            return _gate.Task;
        }

        public void Release() => _gate.TrySetResult();

        public void AttachToCall(ICall call) { }
        public void Detach() { }
        public void Dispose() => _gate.TrySetResult();
    }

    private sealed class CancellationHonouringSender : IMediaSender
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        public async Task SendAsync(MediaFrame frame, CancellationToken ct = default)
        {
            Entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        public void AttachToCall(ICall call) { }
        public void Detach() { }
        public void Dispose() { }
    }
}
