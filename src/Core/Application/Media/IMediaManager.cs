using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Factory and orchestration entrypoint for media routing, recording and playback.
/// </summary>
public interface IMediaManager
{
    /// <summary>
    /// Active recording sessions.
    /// </summary>
    IReadOnlyCollection<IRecordingSession> ActiveRecordings { get; }

    /// <summary>
    /// Active playback sessions.
    /// </summary>
    IReadOnlyCollection<IPlaybackSession> ActivePlaybacks { get; }

    /// <summary>
    /// Creates a detached media receiver.
    /// </summary>
    IMediaReceiver CreateReceiver();

    /// <summary>
    /// Creates a detached media sender.
    /// </summary>
    IMediaSender CreateSender();

    /// <summary>
    /// Creates a detached video receiver for a call's inbound encoded video frames (transport-only;
    /// decode with your own codec). Attach it to a call to start receiving.
    /// </summary>
    IVideoReceiver CreateVideoReceiver();

    /// <summary>
    /// Creates a detached video sender for a call's outbound encoded video frames (transport-only;
    /// encode with your own codec). Attach it to a call, then push frames.
    /// </summary>
    IVideoSender CreateVideoSender();

    /// <summary>
    /// Creates a connector for one-way or two-way media links.
    /// </summary>
    MediaConnector CreateConnector();

    /// <summary>
    /// Starts recording on one active call.
    /// </summary>
    Task<IRecordingSession> StartCallRecordingAsync(
        ICall call,
        RecordingOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts recording on one active conference mix bus.
    /// </summary>
    Task<IRecordingSession> StartConferenceRecordingAsync(
        IMixedMediaBus conference,
        RecordingOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts playback into one active call.
    /// </summary>
    Task<IPlaybackSession> StartCallPlaybackAsync(
        ICall call,
        PlaybackRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Starts playback broadcast into one active conference.
    /// </summary>
    Task<IPlaybackSession> StartConferencePlaybackAsync(
        IMixedMediaBus conference,
        PlaybackRequest request,
        CancellationToken ct = default);
}
