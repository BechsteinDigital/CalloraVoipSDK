using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Modules;

/// <summary>
/// SDK facade for playback features.
/// </summary>
public interface IPlaybackModule
{
    /// <summary>True when this module can be used in the current runtime context.</summary>
    bool IsAvailable { get; }

    /// <summary>Active playback sessions.</summary>
    IReadOnlyCollection<IPlaybackSession> Active { get; }

    /// <summary>Starts playing the requested media into a single call's audio path.</summary>
    /// <param name="call">The call to play audio into.</param>
    /// <param name="request">What to play (source and options).</param>
    /// <param name="ct">Cancels starting the playback.</param>
    /// <returns>The started playback session, used to monitor or stop it.</returns>
    Task<IPlaybackSession> StartCallAsync(ICall call, PlaybackRequest request, CancellationToken ct = default);

    /// <summary>Starts playing the requested media into a mixed media bus (e.g. a conference).</summary>
    /// <param name="bus">The mixed media bus to play audio into.</param>
    /// <param name="request">What to play (source and options).</param>
    /// <param name="ct">Cancels starting the playback.</param>
    /// <returns>The started playback session, used to monitor or stop it.</returns>
    Task<IPlaybackSession> StartMixedBusAsync(IMixedMediaBus bus, PlaybackRequest request, CancellationToken ct = default);
}
