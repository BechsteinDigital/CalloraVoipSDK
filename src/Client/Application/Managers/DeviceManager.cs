using CalloraVoipSdk.Core.Application.Ports.Audio;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime audio device control facade.
/// </summary>
public sealed class DeviceManager
{
    private readonly Func<IAudioDeviceRuntimeControl> _runtime;
    private readonly Action _throwIfDisposed;

    internal DeviceManager(
        Func<IAudioDeviceRuntimeControl> runtime,
        Action throwIfDisposed)
    {
        _runtime = runtime;
        _throwIfDisposed = throwIfDisposed;
    }

    /// <summary>Enumerates the audio capture (microphone) devices currently available.</summary>
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableInputDevices()
    {
        _throwIfDisposed();
        return _runtime().GetAvailableInputDevices();
    }

    /// <summary>Enumerates the audio playback (speaker) devices currently available.</summary>
    public IReadOnlyList<AudioDeviceDescriptor> GetAvailableOutputDevices()
    {
        _throwIfDisposed();
        return _runtime().GetAvailableOutputDevices();
    }

    /// <summary>Returns a snapshot of the current runtime audio state (selected devices, volumes, mute, format).</summary>
    public AudioDeviceRuntimeSnapshot GetRuntimeSnapshot()
    {
        _throwIfDisposed();
        return _runtime().GetRuntimeSnapshot();
    }

    /// <summary>Switches the active capture device.</summary>
    /// <param name="deviceId">Id of the input device to select; <see langword="null"/> selects the platform default.</param>
    public void SwitchInputDevice(string? deviceId)
    {
        _throwIfDisposed();
        _runtime().SwitchInputDevice(deviceId);
    }

    /// <summary>Switches the active playback device.</summary>
    /// <param name="deviceId">Id of the output device to select; <see langword="null"/> selects the platform default.</param>
    public void SwitchOutputDevice(string? deviceId)
    {
        _throwIfDisposed();
        _runtime().SwitchOutputDevice(deviceId);
    }

    /// <summary>Sets the capture (microphone) gain.</summary>
    /// <param name="volume">Linear volume, typically 0.0 (silence) to 1.0 (unity).</param>
    public void SetInputVolume(float volume)
    {
        _throwIfDisposed();
        _runtime().SetInputVolume(volume);
    }

    /// <summary>Sets the playback (speaker) volume.</summary>
    /// <param name="volume">Linear volume, typically 0.0 (silence) to 1.0 (unity).</param>
    public void SetOutputVolume(float volume)
    {
        _throwIfDisposed();
        _runtime().SetOutputVolume(volume);
    }

    /// <summary>Mutes or unmutes audio capture.</summary>
    /// <param name="isMuted"><see langword="true"/> to mute the microphone; <see langword="false"/> to unmute.</param>
    public void SetInputMuted(bool isMuted)
    {
        _throwIfDisposed();
        _runtime().SetInputMuted(isMuted);
    }

    /// <summary>Mutes or unmutes audio playback.</summary>
    /// <param name="isMuted"><see langword="true"/> to mute the speaker; <see langword="false"/> to unmute.</param>
    public void SetOutputMuted(bool isMuted)
    {
        _throwIfDisposed();
        _runtime().SetOutputMuted(isMuted);
    }

    /// <summary>Updates the sample rate/channel/bit-depth format used by the audio device.</summary>
    /// <param name="format">The new device audio format.</param>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    public void UpdateFormat(AudioDeviceFormat format)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(format);
        _runtime().UpdateFormat(format);
    }
}
