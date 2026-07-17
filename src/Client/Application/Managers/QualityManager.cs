using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk;

/// <summary>
/// Runtime quality facade for call-level snapshots and subscriptions.
/// </summary>
public sealed class QualityManager : IQualityManager
{
    internal QualityManager()
    {
    }

    /// <summary>
    /// Returns the latest call quality snapshot.
    /// </summary>
    public CallQualitySnapshot GetSnapshot(ICall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.QualitySnapshot;
    }

    /// <summary>
    /// Subscribes to quality updates for one call.
    /// </summary>
    public IDisposable Subscribe(ICall call, Action<CallQualitySnapshot> handler)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(handler);

        EventHandler<CallQualitySnapshotChangedEventArgs> subscription = (_, args) =>
            handler(args.Snapshot);

        call.QualitySnapshotChanged += subscription;
        return new QualitySubscription(() => call.QualitySnapshotChanged -= subscription);
    }
}
