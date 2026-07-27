namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Arrival-order loss detection for one inbound video stream (WebRTC phase 3, RFC 4585). It classifies each
/// arriving RTP sequence number against the highest seen so far and signals loss on a forward gap — but not
/// immediately: a newly missing sequence is <em>held</em> until the stream advances past it by more than a
/// small reorder tolerance, so a genuinely reordered packet gets a window to arrive first and is never NACKed.
/// This mirrors the mature stacks (libwebrtc <c>NackModule</c>'s reorder threshold, Pion's <c>skipLastN</c>),
/// which never NACK the leading edge synchronously — a spurious NACK/RTX storm on simple reordering is the
/// failure mode a per-packet-immediate NACK causes. A backward step (reorder/duplicate) never regresses the
/// highest-seen reference and clears any pending entry for the arriving sequence.
/// </summary>
internal sealed class VideoArrivalLossTracker
{
    // Beyond this many missing packets a NACK is pointless — the loss is better recovered
    // with a keyframe (PLI), so we stop enumerating and let the throttled PLI carry it.
    private const int MaxEnumeratedLoss = 256;

    // A backward sequence step (a reorder) wraps the forward distance to at least this value;
    // treated as reordering, not loss. Half the 16-bit space is the reorder/loss boundary.
    private const int ReorderBoundary = 0x8000;

    // Hold a newly missing sequence for this many further arrivals before declaring it lost, so a reordered
    // packet has a window to arrive first (libwebrtc NackModule reorder threshold / Pion skipLastN). Live-network
    // reordering is almost always within a couple of packets, so a small window removes the spurious NACK/RTX
    // without materially delaying recovery of a genuine loss; an adaptive threshold is a later refinement.
    private const int ReorderTolerance = 2;

    private ushort _highest;
    private bool _hasReceived;

    // Sequences seen as forward gaps but not yet confirmed lost: held until the highest-seen reference advances
    // past them by more than ReorderTolerance (then reported), or until they arrive reordered (then removed).
    // Bounded by MaxEnumeratedLoss per gap; a larger jump is treated as heavy loss (PLI) and clears it.
    private readonly HashSet<ushort> _pending = new();

    /// <summary>
    /// Records an arriving sequence number and returns the arrival-order loss signal:
    /// <list type="bullet">
    /// <item><see langword="null"/> — the first packet, an in-order/duplicate/reordered arrival, or a forward
    /// gap whose missing sequences are still inside the reorder window (held, not yet reported).</item>
    /// <item>empty — a forward loss burst larger than <see cref="MaxEnumeratedLoss"/>: a PLI only.</item>
    /// <item>a list — the missing sequence numbers that have now aged past the reorder window: NACK them
    /// (plus PLI).</item>
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

        // Any arrival — including a reordered/late one — clears that sequence from the pending set: it is not
        // lost, so it must never be NACKed even though an earlier gap flagged it.
        _pending.Remove(sequence);

        var forwardGap = (ushort)(sequence - _highest);
        if (forwardGap is >= 1 and < ReorderBoundary)
        {
            // A forward jump larger than we enumerate is genuine heavy loss, not reordering: recover it with a
            // keyframe immediately (a NACK for hundreds of packets is pointless), and drop the now-stale pending.
            if (forwardGap - 1 > MaxEnumeratedLoss)
            {
                _highest = sequence;
                _pending.Clear();
                return Array.Empty<ushort>();
            }

            // Otherwise hold the newly missing sequences; they are only NACKed once they age past the reorder
            // window in ReleaseAgedOut below.
            for (var s = (ushort)(_highest + 1); s != sequence; s = (ushort)(s + 1))
                _pending.Add(s);
            _highest = sequence;
        }
        // else: a reorder/duplicate (backward step) — the Remove above already recovered a reordered packet.

        return ReleaseAgedOut();
    }

    // Returns (ascending) and removes the pending sequences the highest-seen reference has now advanced past by
    // more than the reorder tolerance — confirmed lost. Returns null when none have aged out yet.
    private IReadOnlyList<ushort>? ReleaseAgedOut()
    {
        List<ushort>? lost = null;
        foreach (var s in _pending)
        {
            var age = (ushort)(_highest - s);
            if (age is > ReorderTolerance and < ReorderBoundary)
                (lost ??= new List<ushort>()).Add(s);
        }

        if (lost is null)
            return null;

        foreach (var s in lost)
            _pending.Remove(s);

        // Ascending sequence order (oldest missing first) = descending age from the highest-seen reference.
        lost.Sort((a, b) => ((ushort)(_highest - b)).CompareTo((ushort)(_highest - a)));
        return lost;
    }
}
