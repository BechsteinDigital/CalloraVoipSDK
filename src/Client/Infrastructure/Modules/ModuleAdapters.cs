using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Modules;

internal static class ModuleAdapters
{
    public static IConferencingModule CreateConferencing(
        MediaManager media,
        ILoggerFactory loggerFactory)
    {
        return new UnavailableConferencingModule("conferencing");
    }

    public static IPlaybackModule CreatePlayback(MediaManager media)
    {
        return new CorePlaybackModule(media);
    }

    public static IRecordingModule CreateRecording(MediaManager media)
    {
        return new CoreRecordingModule(media);
    }

    public static IRealtimeModule CreateRealtime(
        MediaManager media,
        ILoggerFactory loggerFactory)
    {
        return new UnavailableRealtimeModule("realtime");
    }

    public static IWebSocketModule CreateWebSocketAudioTransport(ILoggerFactory loggerFactory)
    {
        return new UnavailableWebSocketAudioTransportModule("websocket");
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

    public Task DecryptRecordingAsync(
        string encryptedInputPath,
        string decryptedOutputPath,
        IRecordingEncryptionProvider provider,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedInputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(decryptedOutputPath);
        ArgumentNullException.ThrowIfNull(provider);

        // Pure file operation: no active session required, delegates to the provider's
        // streaming decryption. Failures (auth/format) propagate unchanged to the caller.
        return provider.DecryptFileAsync(encryptedInputPath, decryptedOutputPath, ct).AsTask();
    }
}

internal sealed class UnavailableConferencingModule(string moduleId) : IConferencingModule
{
    public bool IsAvailable => false;

    public IReadOnlyCollection<IConferenceSession> Active => [];

    public IConferenceSession Create() => throw new ModuleFeatureUnavailableException(moduleId);
}

internal sealed class UnavailableRealtimeModule(string moduleId) : IRealtimeModule
{
    public bool IsAvailable => false;

    public IReadOnlyCollection<ICallRealtimeBridge> Active => [];

    public Task<ICallRealtimeBridge> StartCallBridgeAsync(
        ICall call,
        IAudioFrameStreamTransport transport,
        RealtimeBridgeOptions? options = null,
        CancellationToken ct = default) =>
        throw new ModuleFeatureUnavailableException(moduleId);
}

internal sealed class UnavailableWebSocketAudioTransportModule(string moduleId) : IWebSocketModule
{
    public bool IsAvailable => false;

    public IWebSocketConnection CreateConnection(
        Uri endpoint,
        WebSocketClientOptions? options = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        throw new ModuleFeatureUnavailableException(moduleId);

    public IAudioFrameStreamTransport Create(
        Uri endpoint,
        WebSocketAudioFrameTransportOptions? options = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        throw new ModuleFeatureUnavailableException(moduleId);
}
