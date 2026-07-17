namespace CalloraVoipSdk.Core.Application.Ports.Audio;

/// <summary>
/// Snapshot of runtime audio device state used by SDK diagnostics and UI controls.
/// </summary>
public sealed class AudioDeviceRuntimeSnapshot
{
    /// <summary>
    /// Creates one runtime state snapshot.
    /// </summary>
    /// <param name="isConnected">True when the audio device is currently connected to media legs.</param>
    /// <param name="inputDeviceId">Selected input device id.</param>
    /// <param name="outputDeviceId">Selected output device id.</param>
    /// <param name="inputMuted">Input mute state.</param>
    /// <param name="outputMuted">Output mute state.</param>
    /// <param name="inputVolume">Input gain factor in range 0..2.</param>
    /// <param name="outputVolume">Output gain factor in range 0..2.</param>
    /// <param name="format">Active capture/playback device format.</param>
    /// <param name="playbackQueueDepth">
    /// Current number of decoded frames buffered in the device's bounded playback queue. Zero for
    /// devices that do not maintain such a queue.
    /// </param>
    /// <param name="droppedPlaybackFrames">
    /// Cumulative number of playback frames dropped by the queue's backpressure policy for the
    /// current connection — a non-zero value indicates the RX path is outpacing playback. Zero for
    /// devices that do not maintain a bounded playback queue.
    /// </param>
    public AudioDeviceRuntimeSnapshot(
        bool isConnected,
        string inputDeviceId,
        string outputDeviceId,
        bool inputMuted,
        bool outputMuted,
        float inputVolume,
        float outputVolume,
        AudioDeviceFormat format,
        int playbackQueueDepth = 0,
        long droppedPlaybackFrames = 0)
    {
        if (string.IsNullOrWhiteSpace(inputDeviceId))
            throw new ArgumentException("Input device id is required.", nameof(inputDeviceId));
        if (string.IsNullOrWhiteSpace(outputDeviceId))
            throw new ArgumentException("Output device id is required.", nameof(outputDeviceId));
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(playbackQueueDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedPlaybackFrames);

        IsConnected = isConnected;
        InputDeviceId = inputDeviceId;
        OutputDeviceId = outputDeviceId;
        InputMuted = inputMuted;
        OutputMuted = outputMuted;
        InputVolume = inputVolume;
        OutputVolume = outputVolume;
        Format = format;
        PlaybackQueueDepth = playbackQueueDepth;
        DroppedPlaybackFrames = droppedPlaybackFrames;
    }

    /// <summary>
    /// True when capture/playback streams are currently active.
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>
    /// Selected input device id.
    /// </summary>
    public string InputDeviceId { get; }

    /// <summary>
    /// Selected output device id.
    /// </summary>
    public string OutputDeviceId { get; }

    /// <summary>
    /// True when microphone input is muted.
    /// </summary>
    public bool InputMuted { get; }

    /// <summary>
    /// True when speaker output is muted.
    /// </summary>
    public bool OutputMuted { get; }

    /// <summary>
    /// Input gain factor in range 0..2.
    /// </summary>
    public float InputVolume { get; }

    /// <summary>
    /// Output gain factor in range 0..2.
    /// </summary>
    public float OutputVolume { get; }

    /// <summary>
    /// Active capture/playback format.
    /// </summary>
    public AudioDeviceFormat Format { get; }

    /// <summary>
    /// Current number of decoded frames buffered in the device's bounded playback queue.
    /// Zero for devices that do not maintain such a queue.
    /// </summary>
    public int PlaybackQueueDepth { get; }

    /// <summary>
    /// Cumulative number of playback frames dropped by the queue's backpressure policy for the
    /// current connection. A non-zero value indicates the receive path is outpacing playback.
    /// Zero for devices that do not maintain a bounded playback queue.
    /// </summary>
    public long DroppedPlaybackFrames { get; }
}
