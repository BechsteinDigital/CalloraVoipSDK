using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One inbound reassembly lane of a <see cref="BundledVideoTrack"/>: an independent depacketiser, reorder
/// window, arrival-loss tracker, and ordered-delivery cursor for a single inbound RTP stream. On a
/// non-simulcast track there is one lane (RID null); on a simulcast receive (RFC 8853) there is one per
/// inbound <c>a=rid</c>, so interleaved encodings never share reorder, loss, or depacketiser state — which
/// would otherwise corrupt reassembly and raise phantom NACKs across the aliased sequence spaces.
/// </summary>
/// <remarks>
/// None of the collaborators is <see cref="IDisposable"/>, so a lane needs no teardown. Touched only from
/// the bundle's single receive loop (the depacketiser and reorder window are stateful and not thread-safe),
/// so it carries no synchronisation of its own.
/// </remarks>
internal sealed class BundledVideoInboundLayer
{
    /// <summary>Builds a lane for one inbound RTP stream.</summary>
    /// <param name="rid">The simulcast <c>a=rid</c> this lane reassembles, or <see langword="null"/> for the default stream.</param>
    /// <param name="depacketiser">The lane's stateful video depacketiser.</param>
    /// <param name="reorderBuffer">The lane's reorder window.</param>
    public BundledVideoInboundLayer(string? rid, IVideoDepacketiser depacketiser, VideoReorderBuffer reorderBuffer)
    {
        Rid = rid;
        Depacketiser = depacketiser ?? throw new ArgumentNullException(nameof(depacketiser));
        ReorderBuffer = reorderBuffer ?? throw new ArgumentNullException(nameof(reorderBuffer));
    }

    /// <summary>The simulcast <c>a=rid</c> this lane reassembles, or <see langword="null"/> for the default stream.</summary>
    public string? Rid { get; }

    /// <summary>The lane's depacketiser (stateful, receive-loop-only).</summary>
    public IVideoDepacketiser Depacketiser { get; }

    /// <summary>The lane's reorder window (receive-loop-only).</summary>
    public VideoReorderBuffer ReorderBuffer { get; }

    /// <summary>Arrival-order loss detection for this lane's SSRC — never shared, or SSRCs alias into phantom gaps.</summary>
    public VideoArrivalLossTracker ArrivalLoss { get; } = new();

    /// <summary>Whether at least one packet has been delivered in order on this lane.</summary>
    public bool HasDelivered { get; set; }

    /// <summary>The last in-order sequence number delivered on this lane.</summary>
    public ushort LastDeliveredSequence { get; set; }
}
