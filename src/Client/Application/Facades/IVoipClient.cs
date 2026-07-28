using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Application.Lines;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Publications;
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

    /// <summary>
    /// Raised when an inbound SIP MESSAGE (RFC 3428 pager-mode instant message) arrives on any registered
    /// line. The SDK has already answered it 200 OK; the handler only consumes the content.
    /// </summary>
    event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

    /// <summary>Raised when any active call changes state.</summary>
    event EventHandler<CallStateChangedEventArgs>? CallStateChanged;

    /// <summary>Registers one line and waits for a terminal connect outcome.</summary>
    [Obsolete("Use ConnectAsync(...) instead. RegisterAndWaitAsync(...) has been deprecated since v1.0 and is kept for backward compatibility; it may be removed in a future major release.", false)]
    Task<ConnectResult> RegisterAndWaitAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default);

    /// <summary>Registers one line and waits for a terminal connect outcome.</summary>
    Task<ConnectResult> ConnectAsync(SipAccount account, ConnectOptions? options = null, CancellationToken ct = default);

    /// <summary>Dials a target and waits until the call reaches connected state.</summary>
    Task<DialResult> DialAndWaitUntilConnectedAsync(IPhoneLine line, string targetUri, DialWaitOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Sends an out-of-dialog SIP MESSAGE (RFC 3428 pager-mode IM) from the first registered line. Register a
    /// line first, or use <see cref="IPhoneLine.SendMessageAsync"/> to send from a specific line.
    /// </summary>
    /// <param name="targetUri">The recipient's SIP URI.</param>
    /// <param name="body">The message text/body.</param>
    /// <param name="contentType">The body's MIME type; defaults to <c>text/plain</c>.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>A task that completes when the peer answers 2xx; it faults on a non-2xx or no response.</returns>
    Task SendMessageAsync(string targetUri, string body, string contentType = "text/plain", CancellationToken ct = default);

    /// <summary>
    /// Publishes event state (RFC 3903, e.g. presence) for the first registered line's address-of-record and
    /// returns the SIP-ETag and granted lifetime. Register a line first, or use
    /// <see cref="IPhoneLine.PublishAsync"/> to publish from a specific line.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="body">The event-state document to publish (for example a PIDF body).</param>
    /// <param name="contentType">The body's MIME type (e.g. <c>application/pidf+xml</c>).</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the publish.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> PublishAsync(string eventType, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Refreshes a prior publication's lifetime (RFC 3903 SIP-If-Match) for the first registered line, retaining
    /// its event state. Register a line first, or use <see cref="IPhoneLine.RefreshPublicationAsync"/> for a
    /// specific line. The <paramref name="etag"/> is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to refresh.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> RefreshPublicationAsync(string eventType, string etag, int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Modifies a prior publication by replacing its body (RFC 3903 SIP-If-Match) for the first registered line.
    /// Register a line first, or use <see cref="IPhoneLine.ModifyPublicationAsync"/> for a specific line. The
    /// <paramref name="etag"/> is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to modify.</param>
    /// <param name="body">The replacement event-state document to publish (for example a PIDF body).</param>
    /// <param name="contentType">The body's MIME type; defaults to <c>text/plain</c>.</param>
    /// <param name="expiresSeconds">Requested publication lifetime in seconds. Defaults to 3600.</param>
    /// <param name="ct">Cancels the modify.</param>
    /// <returns>The assigned entity-tag and granted lifetime; faults on a non-2xx or no response.</returns>
    Task<PublishResult> ModifyPublicationAsync(string eventType, string etag, string body, string contentType = "text/plain", int expiresSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// Removes a prior publication (RFC 3903 SIP-If-Match with Expires: 0) for the first registered line.
    /// Register a line first, or use <see cref="IPhoneLine.RemovePublicationAsync"/> for a specific line. The
    /// <paramref name="etag"/> is the SIP-ETag from a prior PublishAsync/refresh.
    /// </summary>
    /// <param name="eventType">The event package (Event header, e.g. <c>presence</c>).</param>
    /// <param name="etag">The SIP-ETag of the publication to remove.</param>
    /// <param name="ct">Cancels the remove.</param>
    /// <returns>A task that completes when the peer answers 2xx; it faults on a non-2xx or no response.</returns>
    Task RemovePublicationAsync(string eventType, string etag, CancellationToken ct = default);

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
