namespace CalloraVoipSdk.Audio.Windows;

public sealed class AudioDeviceOptions
{
    /// <summary>
    /// WaveIn device number. -1 = default system microphone.
    /// Use <see cref="WindowsAudioDevice.GetInputDevices"/> to enumerate.
    /// </summary>
    public int InputDeviceNumber { get; init; } = -1;

    /// <summary>
    /// WaveOut device number. -1 = default system speaker.
    /// Use <see cref="WindowsAudioDevice.GetOutputDevices"/> to enumerate.
    /// </summary>
    public int OutputDeviceNumber { get; init; } = -1;

    /// <summary>
    /// RTP audio sample rate. Must match the selected codec.
    /// G.711 = 8000, G.722 = 16000.
    /// </summary>
    public int SampleRate { get; init; } = 8000;

    /// <summary>
    /// PCM bit depth. Only 16-bit PCM is supported — the G.711/G.722 codecs and the capture/playback
    /// path assume it — so this must be 16 (the default); any other value is rejected by the device
    /// constructor.
    /// </summary>
    public int BitsPerSample { get; init; } = 16;

    /// <summary>
    /// Channel count. SIP audio is always mono, so this must be 1 (the default); any other value is
    /// rejected by the device constructor.
    /// </summary>
    public int Channels { get; init; } = 1;
}
