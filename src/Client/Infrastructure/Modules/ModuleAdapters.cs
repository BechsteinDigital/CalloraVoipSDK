using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Modules;

internal static class ModuleAdapters
{
    public static IPlaybackModule CreatePlayback(MediaManager media)
    {
        return new CorePlaybackModule(media);
    }

    public static IRecordingModule CreateRecording(MediaManager media)
    {
        return new CoreRecordingModule(media);
    }
}

internal sealed class CorePlaybackModule(MediaManager media) : IPlaybackModule
{
    public bool IsAvailable => true;

    public IReadOnlyCollection<IPlaybackSession> Active => media.ActivePlaybacks;

    public Task<IPlaybackSession> StartCallAsync(ICall call, PlaybackRequest request, CancellationToken ct = default) =>
        media.StartCallPlaybackAsync(call, request, ct);

    public Task<IPlaybackSession> StartMixedBusAsync(IMixedMediaBus bus, PlaybackRequest request, CancellationToken ct = default) =>
        media.StartConferencePlaybackAsync(bus, request, ct);
}

internal sealed class CoreRecordingModule(MediaManager media) : IRecordingModule
{
    public bool IsAvailable => true;

    public IReadOnlyCollection<IRecordingSession> Active => media.ActiveRecordings;

    public Task<IRecordingSession> StartCallAsync(ICall call, RecordingOptions? options = null, CancellationToken ct = default) =>
        media.StartCallRecordingAsync(call, options, ct);

    public Task<IRecordingSession> StartMixedBusAsync(IMixedMediaBus bus, RecordingOptions? options = null, CancellationToken ct = default) =>
        media.StartConferenceRecordingAsync(bus, options, ct);
}
