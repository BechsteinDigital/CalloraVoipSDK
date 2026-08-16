using CalloraVoipSdk.Core.Application.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The media manager owns the sessions it starts (#165 P2-8). A recording session holds an open file whose
/// container still needs finalising and a playback session holds a running loop pushing frames into a call, so
/// shutting the SDK down has to stop them: before this the manager implemented no disposal at all and nothing
/// drained its two session maps, leaving both kinds running with nothing holding a reference to stop them.
/// </summary>
public sealed class MediaManagerShutdownOwnershipTests
{
    private static MediaManager Manager() => new(NullLoggerFactory.Instance);

    [Fact]
    public async Task Shutdown_disposes_every_tracked_playback_and_recording_session()
    {
        var manager = Manager();
        var playback = new FakePlaybackSession();
        var recording = new FakeRecordingSession();
        manager.TrackPlaybackSession(playback);
        manager.TrackRecordingSession(recording);

        await manager.DisposeAsync();

        Assert.True(playback.Disposed, "a running playback session must be stopped on shutdown");
        Assert.True(recording.Disposed, "a recording session must be finalised on shutdown");
        Assert.Empty(manager.ActivePlaybacks);
        Assert.Empty(manager.ActiveRecordings);
    }

    [Fact]
    public async Task One_faulting_session_does_not_strand_the_others()
    {
        var manager = Manager();
        var faulty = new FakePlaybackSession { ThrowOnDispose = true };
        var healthy = new FakePlaybackSession();
        var recording = new FakeRecordingSession();
        manager.TrackPlaybackSession(faulty);
        manager.TrackPlaybackSession(healthy);
        manager.TrackRecordingSession(recording);

        await manager.DisposeAsync();

        Assert.True(healthy.Disposed);
        Assert.True(recording.Disposed, "the recording drain must not be skipped by a faulting playback session");
    }

    [Fact]
    public async Task Shutdown_is_idempotent_and_refuses_new_sessions_afterwards()
    {
        var manager = Manager();
        var playback = new FakePlaybackSession();
        manager.TrackPlaybackSession(playback);

        await manager.DisposeAsync();
        await manager.DisposeAsync(); // no second drain, no throw

        Assert.Equal(1, playback.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => manager.TrackPlaybackSession(new FakePlaybackSession()));
        Assert.Throws<ObjectDisposedException>(() => manager.TrackRecordingSession(new FakeRecordingSession()));
    }

    private sealed class FakePlaybackSession : IPlaybackSession
    {
        public bool ThrowOnDispose { get; init; }
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;

        public Guid SessionId { get; } = Guid.NewGuid();
        public MediaSessionState State => MediaSessionState.Running;
        public string SourceFilePath => "in-memory";
        public AudioFileFormat Format => AudioFileFormat.Wav;

        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ThrowOnDispose
                ? ValueTask.FromException(new InvalidOperationException("playback teardown failed"))
                : ValueTask.CompletedTask;
        }

#pragma warning disable CS0067 // the manager subscribes; the tests never raise
        public event EventHandler<MediaSessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaSessionErrorEventArgs>? Error;
#pragma warning restore CS0067
    }

    private sealed class FakeRecordingSession : IRecordingSession
    {
        public bool Disposed { get; private set; }

        public Guid SessionId { get; } = Guid.NewGuid();
        public MediaSessionState State => MediaSessionState.Running;
        public AudioFileFormat Format => AudioFileFormat.Wav;
        public IReadOnlyList<string> OutputFiles => [];

        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

#pragma warning disable CS0067
        public event EventHandler<MediaSessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<MediaSessionErrorEventArgs>? Error;
#pragma warning restore CS0067
    }
}
