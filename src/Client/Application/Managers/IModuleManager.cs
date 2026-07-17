using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime availability facade for optional SDK modules.
/// </summary>
public interface IModuleManager
{
    /// <summary>
    /// Current playback module facade.
    /// </summary>
    IPlaybackModule Playback { get; }

    /// <summary>
    /// Current recording module facade.
    /// </summary>
    IRecordingModule Recording { get; }

    /// <summary>
    /// True when playback is available.
    /// </summary>
    bool PlaybackAvailable { get; }

    /// <summary>
    /// True when recording is available.
    /// </summary>
    bool RecordingAvailable { get; }
}
