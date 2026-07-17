using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk;

/// <summary>
/// Read-only runtime view over active calls and media sessions.
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// Active non-terminated calls.
    /// </summary>
    IReadOnlyCollection<ICall> ActiveCalls { get; }

    /// <summary>
    /// Active playback sessions.
    /// </summary>
    IReadOnlyCollection<IPlaybackSession> ActivePlaybacks { get; }

    /// <summary>
    /// Active recording sessions.
    /// </summary>
    IReadOnlyCollection<IRecordingSession> ActiveRecordings { get; }
}
