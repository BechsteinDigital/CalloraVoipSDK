using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Factory and orchestration entrypoint for media routing, recording and playback.
/// </summary>
public sealed class MediaManager
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MediaManager> _logger;
    private readonly IAudioFileCodecRegistry _audioFileCodecs;
    private readonly ConcurrentDictionary<Guid, IRecordingSession> _activeRecordings = new();
    private readonly ConcurrentDictionary<Guid, IPlaybackSession> _activePlaybacks = new();

    /// <summary>
    /// Creates a media manager instance.
    /// </summary>
    internal MediaManager(
        ILoggerFactory? loggerFactory = null,
        IAudioFileCodecRegistry? audioFileCodecs = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<MediaManager>();
        _audioFileCodecs = audioFileCodecs ?? EmptyAudioFileCodecRegistry.Instance;
    }

    /// <summary>
    /// Active recording sessions.
    /// </summary>
    public IReadOnlyCollection<IRecordingSession> ActiveRecordings => _activeRecordings.Values.ToArray();

    /// <summary>
    /// Active playback sessions.
    /// </summary>
    public IReadOnlyCollection<IPlaybackSession> ActivePlaybacks => _activePlaybacks.Values.ToArray();

    /// <summary>
    /// Creates a detached media receiver.
    /// </summary>
    public IMediaReceiver CreateReceiver() => new MediaReceiver(_loggerFactory.CreateLogger<MediaReceiver>());

    /// <summary>
    /// Creates a detached media sender.
    /// </summary>
    public IMediaSender CreateSender() => new MediaSender(_loggerFactory.CreateLogger<MediaSender>());

    /// <summary>
    /// Creates a connector for one-way or two-way media links.
    /// </summary>
    public MediaConnector CreateConnector() => new(_loggerFactory);

    /// <summary>
    /// Starts recording on one active call.
    /// </summary>
    public async Task<IRecordingSession> StartCallRecordingAsync(
        ICall call,
        RecordingOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        options ??= new RecordingOptions();

        if (!TryResolveCodec(options.Format, out var codec))
            throw new InvalidOperationException($"No audio file codec registered for format {options.Format}.");

        if (!AudioPayloadTranscoder.TryCreateForCall(
                call,
                options.Format,
                options.SampleRateHz,
                options.SamplesPerFrame,
                out var plan,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var source = new CallRecordingFrameSource(
            this,
            call,
            _loggerFactory.CreateLogger<CallRecordingFrameSource>());

        var session = new RecordingSession(
            source,
            codec,
            plan!,
            options,
            _loggerFactory.CreateLogger<RecordingSession>(),
            startPaused: false);

        TrackRecordingSession(session);

        try
        {
            await session.StartAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts recording on one active conference mix bus.
    /// </summary>
    public async Task<IRecordingSession> StartConferenceRecordingAsync(
        IMixedMediaBus conference,
        RecordingOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conference);

        options ??= new RecordingOptions();

        if (!TryResolveCodec(options.Format, out var codec))
            throw new InvalidOperationException($"No audio file codec registered for format {options.Format}.");

        if (!AudioPayloadTranscoder.TryCreateForConference(
                options.Format,
                options.SampleRateHz,
                options.SamplesPerFrame,
                out var plan,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var source = new ConferenceRecordingFrameSource(
            conference,
            _loggerFactory.CreateLogger<ConferenceRecordingFrameSource>());

        var session = new RecordingSession(
            source,
            codec,
            plan!,
            options,
            _loggerFactory.CreateLogger<RecordingSession>(),
            startPaused: false);

        TrackRecordingSession(session);

        try
        {
            await session.StartAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts playback into one active call.
    /// </summary>
    public Task<IPlaybackSession> StartCallPlaybackAsync(
        ICall call,
        PlaybackRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return StartCallPlaybackCoreAsync(call, request, ct);
    }

    /// <summary>
    /// Starts playback broadcast into one active conference.
    /// </summary>
    public Task<IPlaybackSession> StartConferencePlaybackAsync(
        IMixedMediaBus conference,
        PlaybackRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conference);
        return StartConferencePlaybackCoreAsync(conference, request, ct);
    }

    private async Task<IPlaybackSession> StartCallPlaybackCoreAsync(
        ICall call,
        PlaybackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.FilePath))
            throw new FileNotFoundException("Playback file not found.", request.FilePath);
        var format = ResolvePlaybackFormat(request);

        if (!TryResolveCodec(format, out var codec))
            throw new InvalidOperationException($"No audio file codec registered for format {format}.");

        if (!AudioPayloadTranscoder.TryCreateForCall(
                call,
                format,
                request.SampleRateHz,
                request.Options.SamplesPerFrame,
                out var plan,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var sink = new CallPlaybackFrameSink(
            this,
            call,
            _loggerFactory.CreateLogger<CallPlaybackFrameSink>());

        var session = new PlaybackSession(
            sink,
            codec,
            plan!,
            request,
            format,
            _loggerFactory.CreateLogger<PlaybackSession>());

        TrackPlaybackSession(session);

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return session;
    }

    private async Task<IPlaybackSession> StartConferencePlaybackCoreAsync(
        IMixedMediaBus conference,
        PlaybackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.FilePath))
            throw new FileNotFoundException("Playback file not found.", request.FilePath);
        var format = ResolvePlaybackFormat(request);

        if (!TryResolveCodec(format, out var codec))
            throw new InvalidOperationException($"No audio file codec registered for format {format}.");

        if (!AudioPayloadTranscoder.TryCreateForConference(
                format,
                request.SampleRateHz,
                request.Options.SamplesPerFrame,
                out var plan,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var sink = new ConferencePlaybackFrameSink(conference);

        var session = new PlaybackSession(
            sink,
            codec,
            plan!,
            request,
            format,
            _loggerFactory.CreateLogger<PlaybackSession>());

        TrackPlaybackSession(session);

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return session;
    }

    private bool TryResolveCodec(AudioFileFormat format, out IAudioFileCodec codec)
        => _audioFileCodecs.TryGetCodec(format, out codec!);

    private static AudioFileFormat ResolvePlaybackFormat(PlaybackRequest request)
    {
        if (request.Format is { } explicitFormat)
            return explicitFormat;

        var extension = Path.GetExtension(request.FilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new InvalidOperationException(
                "Playback format could not be inferred from file extension. Set PlaybackRequest.Format explicitly.");
        }

        return extension.Trim().ToLowerInvariant() switch
        {
            ".wav" => AudioFileFormat.Wav,
            ".mp3" => AudioFileFormat.Mp3,
            _ => throw new InvalidOperationException(
                $"Playback format '{extension}' is not supported. Use .wav or .mp3 extension or set explicit format."),
        };
    }

    private void TrackRecordingSession(IRecordingSession session)
    {
        _activeRecordings[session.SessionId] = session;
        session.StateChanged += OnRecordingStateChanged;
    }

    private void TrackPlaybackSession(IPlaybackSession session)
    {
        _activePlaybacks[session.SessionId] = session;
        session.StateChanged += OnPlaybackStateChanged;
    }

    private void OnRecordingStateChanged(object? sender, MediaSessionStateChangedEventArgs args)
    {
        if (sender is not IRecordingSession session)
            return;

        if (args.NewState is not (MediaSessionState.Stopped or MediaSessionState.Faulted))
            return;

        _activeRecordings.TryRemove(session.SessionId, out _);
        session.StateChanged -= OnRecordingStateChanged;
        _logger.LogDebug(
            "Recording session {SessionId} transitioned to {State} ({Reason}).",
            session.SessionId,
            args.NewState,
            args.Reason);
    }

    private void OnPlaybackStateChanged(object? sender, MediaSessionStateChangedEventArgs args)
    {
        if (sender is not IPlaybackSession session)
            return;

        if (args.NewState is not (MediaSessionState.Stopped or MediaSessionState.Faulted))
            return;

        _activePlaybacks.TryRemove(session.SessionId, out _);
        session.StateChanged -= OnPlaybackStateChanged;
        _logger.LogDebug(
            "Playback session {SessionId} transitioned to {State} ({Reason}).",
            session.SessionId,
            args.NewState,
            args.Reason);
    }

}
