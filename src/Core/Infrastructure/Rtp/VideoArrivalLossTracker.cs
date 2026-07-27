namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Arrival-order loss detection for one inbound video stream (WebRTC phase 3, RFC 4585). It classifies each
/// arriving RTP sequence number against the highest seen so far and signals loss only on a genuine forward gap;
/// a reorder or duplicate raises nothing (the reorder window corrects it downstream). The reference advances to
/// the <em>highest</em> sequence seen — never regressing on a reordered/duplicate packet — so a single reorder
/// costs at most one signal instead of a spurious NACK/PLI cascade on the packets that follow it.
/// </summary>
internal sealed class VideoArrivalLossTracker
{
    // Beyond this many missing packets a NACK is pointless — the loss is better recovered
    // with a keyframe (PLI), so we stop enumerating and let the throttled PLI carry it.
    private const int MaxEnumeratedLoss = 256;

    // A backward sequence step (a reorder) wraps the forward distance to at least this value;
    // treated as reordering, not loss. Half the 16-bit space is the reorder/loss boundary.
    private const int ReorderBoundary = 0x8000;

    private ushort _highest;
    private bool _hasReceived;

    /// <summary>
    /// Records an arriving sequence number and returns the arrival-order loss signal:
    /// <list type="bullet">
    /// <item><see langword="null"/> — the first packet, or an in-order / duplicate / reordered arrival:
    /// nothing to report.</item>
    /// <item>empty — a forward loss burst larger than <see cref="MaxEnumeratedLoss"/>: a PLI only.</item>
    /// <item>a list — the missing sequence numbers of a small forward gap: NACK them (plus PLI).</item>
    /// </list>
    /// The highest-seen reference advances only on forward progress, so a reordered packet that momentarily
    /// dips the sequence back does not make the next in-order packet read as a fresh forward gap.
    /// </summary>
    public IReadOnlyList<ushort>? Track(ushort sequence)
    {
        if (!_hasReceived)
        {
            _highest = sequence;
            _hasReceived = true;
            return null;
        }

        var report = LossReport(_highest, sequence);

        // Advance to the highest sequence seen (serial arithmetic); a backward step (reorder) or a
        // duplicate must not drag the reference back — that is what would otherwise turn the next
        // in-order packet into a spurious forward gap.
        var forwardGap = (ushort)(sequence - _highest);
        if (forwardGap is >= 1 and < ReorderBoundary)
            _highest = sequence;

        return report;
    }

    /// <summary>
    /// Classifies a newly arrived sequence number against the highest one for loss reporting. A forward loss
    /// burst of at least half the sequence space (≥ <see cref="ReorderBoundary"/>) is indistinguishable from a
    /// backward step under 16-bit serial-number arithmetic and is therefore treated as a reorder — a
    /// pathological case that never arises in a live stream. Internal for testing.
    /// </summary>
    internal static IReadOnlyList<ushort>? LossReport(ushort last, ushort current)
    {
        var gap = (ushort)(current - last); // forward distance; a reorder wraps to a large value
        if (gap < 2 || gap >= ReorderBoundary)
            return null; // in-order (1), duplicate (0), or reorder (backward step)

        if (gap > MaxEnumeratedLoss)
            return Array.Empty<ushort>(); // forward loss too large to enumerate → PLI only

        var missing = new ushort[gap - 1];
        for (var i = 0; i < missing.Length; i++)
            missing[i] = (ushort)(last + i + 1);
        return missing;
    }
}
