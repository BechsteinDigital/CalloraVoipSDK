using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Modules;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime availability facade for optional SDK modules.
/// </summary>
public sealed class ModuleManager
{
    internal ModuleManager(MediaManager media)
    {
        Playback = ModuleAdapters.CreatePlayback(media);
        Recording = ModuleAdapters.CreateRecording(media);
    }

    /// <summary>
    /// Current playback module facade.
    /// </summary>
    public IPlaybackModule Playback { get; }

    /// <summary>
    /// Current recording module facade.
    /// </summary>
    public IRecordingModule Recording { get; }

    /// <summary>
    /// True when playback is available.
    /// </summary>
    public bool PlaybackAvailable => Playback.IsAvailable;

    /// <summary>
    /// True when recording is available.
    /// </summary>
    public bool RecordingAvailable => Recording.IsAvailable;
}
