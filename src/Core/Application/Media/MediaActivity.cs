using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Mutable per-call inbound-activity tracker used by <see cref="CallMediaOrchestrator"/> to
/// hang up a call whose inbound RTP has gone silent. Mutable fields are read/written from the
/// metrics callback; <see cref="HungUp"/> is guarded with <see cref="System.Threading.Interlocked"/>
/// so the hangup fires at most once.
/// </summary>
internal sealed class MediaActivity
{
    /// <summary>The call this tracker belongs to.</summary>
    public required ICall Call { get; init; }

    /// <summary>Last observed inbound packet count.</summary>
    public long LastReceived;

    /// <summary>Timestamp of the last observed inbound-media progress.</summary>
    public DateTimeOffset LastActivityUtc;

    /// <summary>Once-guard for the media-timeout hangup (0 = not yet, 1 = fired).</summary>
    public int HungUp;
}
