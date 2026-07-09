using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Modules;

/// <summary>
/// SDK facade for recording features.
/// </summary>
public interface IRecordingModule
{
    /// <summary>True when this module can be used in the current runtime context.</summary>
    bool IsAvailable { get; }

    /// <summary>Active recording sessions.</summary>
    IReadOnlyCollection<IRecordingSession> Active { get; }

    /// <summary>Starts recording a single call's audio.</summary>
    /// <param name="call">The call to record.</param>
    /// <param name="options">Recording options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancels starting the recording.</param>
    /// <returns>The started recording session, used to monitor or stop it.</returns>
    Task<IRecordingSession> StartCallAsync(ICall call, RecordingOptions? options = null, CancellationToken ct = default);

    /// <summary>Starts recording a mixed media bus (e.g. a conference mix).</summary>
    /// <param name="bus">The mixed media bus to record.</param>
    /// <param name="options">Recording options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancels starting the recording.</param>
    /// <returns>The started recording session, used to monitor or stop it.</returns>
    Task<IRecordingSession> StartMixedBusAsync(IMixedMediaBus bus, RecordingOptions? options = null, CancellationToken ct = default);
}
