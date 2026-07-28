namespace CalloraVoipSdk.Audio.Linux;

public sealed class AudioDeviceOptions
{
    /// <summary>
    /// PortAudio input device index. -1 = default system microphone.
    /// Use <see cref="LinuxAudioDevice.GetInputDevices"/> to enumerate.
    /// </summary>
    public int InputDeviceIndex { get; init; } = -1;

    /// <summary>
    /// PortAudio output device index. -1 = default system speaker.
    /// Use <see cref="LinuxAudioDevice.GetOutputDevices"/> to enumerate.
    /// </summary>
    public int OutputDeviceIndex { get; init; } = -1;

    /// <summary>G.711 = 8000 Hz. Must match negotiated codec.</summary>
    public int SampleRate { get; init; } = 8000;

    /// <summary>
    /// Explicit override for the PortAudio callback buffer size, in frames. Leave at the default
    /// <c>0</c> to derive a 20 ms buffer from the active sample rate (160 @ 8 kHz, 320 @ 16 kHz);
    /// set a positive value to force a fixed buffer size regardless of sample rate.
    /// </summary>
    public uint FramesPerBuffer { get; init; }
}
