using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Public SDK client contract for dependency-injection and consumer testability.
/// </summary>
public interface IVoipClient : IDisposable
{
    /// <summary>Active call manager for this SDK instance.</summary>
    ICallManager Calls { get; }

    /// <summary>Registered line manager for this SDK instance.</summary>
    IPhoneLineManager Lines { get; }

    /// <summary>Media manager for sender/receiver/connector orchestration.</summary>
    IMediaManager Media { get; }

    /// <summary>Playback module facade.</summary>
    IPlaybackModule PlaybackManager { get; }

    /// <summary>Recording module facade.</summary>
    IRecordingModule RecordingManager { get; }

    /// <summary>Module availability facade.</summary>
    IModuleManager ModuleManager { get; }

    /// <summary>Registry resolving optional modules contributed by separate packages.</summary>
    IModuleRegistry Modules { get; }

    /// <summary>Runtime session view facade.</summary>
    ISessionManager SessionManager { get; }

    /// <summary>Runtime audio-device facade.</summary>
    IDeviceManager DeviceManager { get; }

    /// <summary>Runtime quality facade.</summary>
    IQualityManager QualityManager { get; }

    /// <summary>Runtime policy facade.</summary>
    IPolicyManager PolicyManager { get; }

    /// <summary>Runtime telemetry facade.</summary>
    ITelemetryManager TelemetryManager { get; }

    /// <summary>Raised when a new inbound call arrives on any registered line.</summary>
    event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>Raised when any active call changes state.</summary>
    event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>Registers one line and waits for a terminal connect outcome.</summary>
    [Obsolete("Use ConnectAsync(...) instead. RegisterAndWaitAsync(...) will be removed after v1.0.", false)]
    Task<ConnectResult> RegisterAndWaitAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default);

    /// <summary>Registers one line and waits for a terminal connect outcome.</summary>
    Task<ConnectResult> ConnectAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default);

    /// <summary>Dials a target and waits until the call reaches connected state.</summary>
    Task<DialResult> DialAndWaitUntilConnectedAsync(IPhoneLine line, string targetUri, DialWaitOptions? options = null, CancellationToken ct = default);

    /// <summary>Attaches default audio routing to the specified call.</summary>
    Task AttachDefaultAudioAsync(ICall call, CancellationToken ct = default);

    /// <summary>Detaches default audio routing from the specified call.</summary>
    Task DetachDefaultAudioAsync(ICall call, CancellationToken ct = default);

    /// <summary>
    /// Attaches default video routing to the specified call. Requires an <c>IVideoDevice</c> registered via
    /// dependency injection (the SDK is transport-only and ships no codec); fails closed otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">No video codec device is registered.</exception>
    Task AttachDefaultVideoAsync(ICall call, CancellationToken ct = default);

    /// <summary>Detaches default video routing from the specified call.</summary>
    Task DetachDefaultVideoAsync(ICall call, CancellationToken ct = default);

    /// <summary>Lists runtime-selectable input devices.</summary>
    IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputAudioDevices();

    /// <summary>Lists runtime-selectable output devices.</summary>
    IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputAudioDevices();

    /// <summary>Returns the current runtime audio-device snapshot.</summary>
    AudioDeviceRuntimeSnapshot GetAudioDeviceRuntimeSnapshot();

    /// <summary>Switches the configured SDK input device at runtime.</summary>
    void SwitchAudioInputDevice(string? deviceId);

    /// <summary>Switches the configured SDK output device at runtime.</summary>
    void SwitchAudioOutputDevice(string? deviceId);

    /// <summary>Sets runtime input gain for the configured SDK audio device.</summary>
    void SetAudioInputVolume(float volume);

    /// <summary>Sets runtime output gain for the configured SDK audio device.</summary>
    void SetAudioOutputVolume(float volume);

    /// <summary>Mutes or unmutes runtime microphone capture.</summary>
    void SetAudioInputMuted(bool isMuted);

    /// <summary>Mutes or unmutes runtime speaker playback.</summary>
    void SetAudioOutputMuted(bool isMuted);

    /// <summary>Updates runtime capture/playback format for the configured SDK audio device.</summary>
    void UpdateAudioFormat(AudioDeviceFormat format);

    /// <summary>Registers a simplified asynchronous inbound call handler.</summary>
    IDisposable OnIncomingCall(Func<ICall, Task> handler);
}
