using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime quality facade for call-level snapshots and subscriptions.
/// </summary>
public interface IQualityManager
{
    /// <summary>
    /// Returns the latest call quality snapshot.
    /// </summary>
    CallQualitySnapshot GetSnapshot(ICall call);

    /// <summary>
    /// Subscribes to quality updates for one call.
    /// </summary>
    IDisposable Subscribe(ICall call, Action<CallQualitySnapshot> handler);
}
