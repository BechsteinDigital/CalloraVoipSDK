using System.Reflection;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that <see cref="MediaConnection"/> disposal is deadlock-free and deterministic:
/// <see cref="MediaConnection.DisposeAsync"/> drains the pump loop, the synchronous
/// <see cref="MediaConnection.Dispose"/> never blocks on it, and both are idempotent.
/// </summary>
public sealed class MediaConnectionDisposalTests
{
    private static readonly MediaFrame SampleFrame = new(new byte[] { 1, 2, 3 }, PayloadType: 0, DurationRtpUnits: 160);

    [Fact]
    public async Task DisposeAsync_DrainsPumpLoop_AndIsIdempotent()
    {
        var receiver = new RaisingMediaReceiver();
        var sender = new CountingMediaSender();
        var connection = new MediaConnection(receiver, sender, queueCapacity: 8);

        receiver.Raise(SampleFrame);
        await WaitForAsync(() => sender.SendCount == 1);

        // DisposeAsync completing within the timeout proves the pump loop terminated.
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var pumpTask = (Task)typeof(MediaConnection)
            .GetField("_pumpTask", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(connection)!;
        Assert.True(pumpTask.IsCompleted);
        Assert.False(pumpTask.IsFaulted);

        // Frames arriving after disposal are ignored.
        receiver.Raise(SampleFrame);
        await Task.Delay(50);
        Assert.Equal(1, sender.SendCount);

        // Idempotent second async dispose and mixed sync dispose must not throw.
        await connection.DisposeAsync();
        connection.Dispose();
    }

    [Fact]
    public void Dispose_ReturnsPromptly_AndIsIdempotentWithDisposeAsync()
    {
        var receiver = new RaisingMediaReceiver();
        var sender = new CountingMediaSender();
        var connection = new MediaConnection(receiver, sender, queueCapacity: 8);

        // Synchronous dispose must return without awaiting the pump task.
        connection.Dispose();

        // A subsequent async dispose after sync dispose must be a safe no-op.
        connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }
}

/// <summary>
/// Test double media receiver that raises <see cref="IMediaReceiver.FrameReceived"/> on demand.
/// </summary>
internal sealed class RaisingMediaReceiver : IMediaReceiver
{
    /// <inheritdoc />
    public event EventHandler<MediaFrameReceivedEventArgs>? FrameReceived;

    public void Raise(MediaFrame frame) => FrameReceived?.Invoke(this, new MediaFrameReceivedEventArgs(frame));

    /// <inheritdoc />
    public void AttachToCall(ICall call) { }

    /// <inheritdoc />
    public void Detach() { }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// Test double media sender that counts forwarded frames.
/// </summary>
internal sealed class CountingMediaSender : IMediaSender
{
    private int _count;

    public int SendCount => Volatile.Read(ref _count);

    /// <inheritdoc />
    public void AttachToCall(ICall call) { }

    /// <inheritdoc />
    public void Detach() { }

    /// <inheritdoc />
    public Task SendAsync(MediaFrame frame, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() { }
}
