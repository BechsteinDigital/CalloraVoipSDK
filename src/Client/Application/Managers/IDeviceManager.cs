using CalloraVoipSdk.Core.Application.Ports.Audio;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime audio device control facade.
/// </summary>
public interface IDeviceManager
{
    /// <summary>Enumerates the audio capture (microphone) devices currently available.</summary>
    IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputDevices();

    /// <summary>Enumerates the audio playback (speaker) devices currently available.</summary>
    IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputDevices();

    /// <summary>Returns a snapshot of the current runtime audio state (selected devices, volumes, mute, format).</summary>
    AudioDeviceRuntimeSnapshot GetRuntimeSnapshot();

    /// <summary>Switches the active capture device.</summary>
    /// <param name="deviceId">Id of the input device to select; <see langword="null"/> selects the platform default.</param>
    void SwitchInputDevice(string? deviceId);

    /// <summary>Switches the active playback device.</summary>
    /// <param name="deviceId">Id of the output device to select; <see langword="null"/> selects the platform default.</param>
    void SwitchOutputDevice(string? deviceId);

    /// <summary>Sets the capture (microphone) gain.</summary>
    /// <param name="volume">Linear volume, typically 0.0 (silence) to 1.0 (unity).</param>
    void SetInputVolume(float volume);

    /// <summary>Sets the playback (speaker) volume.</summary>
    /// <param name="volume">Linear volume, typically 0.0 (silence) to 1.0 (unity).</param>
    void SetOutputVolume(float volume);

    /// <summary>Mutes or unmutes audio capture.</summary>
    /// <param name="isMuted"><see langword="true"/> to mute the microphone; <see langword="false"/> to unmute.</param>
    void SetInputMuted(bool isMuted);

    /// <summary>Mutes or unmutes audio playback.</summary>
    /// <param name="isMuted"><see langword="true"/> to mute the speaker; <see langword="false"/> to unmute.</param>
    void SetOutputMuted(bool isMuted);

    /// <summary>Updates the sample rate/channel/bit-depth format used by the audio device.</summary>
    /// <param name="format">The new device audio format.</param>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    void UpdateFormat(AudioDeviceFormat format);
}
