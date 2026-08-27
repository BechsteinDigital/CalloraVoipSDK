using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One inbound audio m-line's jitter buffer plus the synchronisation source it is currently holding
/// packets for, so a source change can be noticed and the buffer reset before the new numbering is
/// mistaken for wild reordering.
/// </summary>
internal sealed class AudioJitterBufferEntry(IJitterBuffer buffer)
{
    /// <summary>The buffer packets for this m-line go through.</summary>
    public IJitterBuffer Buffer { get; } = buffer;

    /// <summary>The SSRC seen so far; null until the first packet arrives.</summary>
    public uint? Ssrc { get; set; }
}
