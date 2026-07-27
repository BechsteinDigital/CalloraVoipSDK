using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #6: the arrival-order loss tracker holds a newly missing sequence for a small reorder window
/// before declaring it lost, so a genuinely reordered packet that arrives within that window is never NACKed —
/// matching libwebrtc's NackModule reorder threshold and Pion's skipLastN (a per-packet-immediate NACK causes a
/// spurious NACK/RTX storm on ordinary reordering). It advances to the highest sequence seen and never regresses
/// on a reorder/duplicate. These pin the deferred, reorder-tolerant behaviour end to end.
/// </summary>
public sealed class VideoArrivalLossTrackerTests
{
    private static IReadOnlyList<ushort>?[] TrackAll(params ushort[] sequences)
    {
        var tracker = new VideoArrivalLossTracker();
        var results = new IReadOnlyList<ushort>?[sequences.Length];
        for (var i = 0; i < sequences.Length; i++)
            results[i] = tracker.Track(sequences[i]);
        return results;
    }

    [Fact]
    public void The_first_arrival_reports_no_loss()
        => Assert.Null(new VideoArrivalLossTracker().Track(100));

    [Fact]
    public void An_in_order_stream_reports_no_loss()
        => Assert.All(TrackAll(100, 101, 102, 103), Assert.Null);

    [Fact]
    public void A_duplicate_reports_no_loss()
        => Assert.All(TrackAll(100, 100), Assert.Null);

    [Fact]
    public void A_gap_is_held_and_only_reported_after_the_reorder_window()
    {
        // 101 is missing at 102, but it is not NACKed immediately — it is held until the stream advances past it
        // by more than the reorder tolerance (2). Only on 104 (age 3) is it declared lost.
        var results = TrackAll(100, 102, 103, 104);

        Assert.Null(results[0]);                    // 100
        Assert.Null(results[1]);                    // 102 — 101 missing but held
        Assert.Null(results[2]);                    // 103 — still inside the reorder window
        Assert.Equal((ushort[])[101], results[3]);  // 104 — 101 has now aged out → NACK
    }

    [Fact]
    public void A_packet_reordered_within_the_window_is_never_reported()
    {
        // 4 arrives before 3, then 3 arrives (reordered) while still inside the reorder window — the whole
        // episode must produce no signal at all. A per-packet-immediate tracker would have NACKed 3 on the gap.
        var results = TrackAll(1, 2, 4, 3, 5, 6, 7);

        Assert.All(results, Assert.Null); // the reordered 3 is recovered before it ages out → zero NACKs
    }

    [Fact]
    public void A_reorder_does_not_regress_the_reference_and_the_reordered_packet_is_not_nacked()
    {
        // Highest reaches 103 (101,102 pending); then a stale 101 arrives (reordered) and is dropped from the
        // pending set. 102 is genuinely lost and is reported once it ages out (105); 101 is never NACKed, and
        // the reference never regressed to 101.
        var results = TrackAll(100, 103, 101, 104, 105);

        Assert.Null(results[0]);                    // 100
        Assert.Null(results[1]);                    // 103 — 101,102 held
        Assert.Null(results[2]);                    // 101 reordered — removed from pending, no signal
        Assert.Null(results[3]);                    // 104 — 102 still inside the window
        Assert.Equal((ushort[])[102], results[4]);  // 105 — only 102 (truly lost) aged out; 101 was recovered
    }

    [Fact]
    public void A_large_forward_jump_reports_a_pli_only_immediately()
    {
        // A jump beyond the enumeration boundary is heavy loss, not reordering: signal a PLI-only (empty) at
        // once rather than holding hundreds of pointless NACK candidates.
        var results = TrackAll(100, (ushort)(100 + 258));

        Assert.Null(results[0]);
        Assert.NotNull(results[1]);
        Assert.Empty(results[1]!); // empty = PLI only
    }

    [Fact]
    public void A_gap_across_the_wrap_boundary_is_reported_after_the_window()
    {
        // 65535 → 1 is a forward distance of 2 (the missing packet is 0). It ages out three arrivals later.
        var results = TrackAll(65535, 1, 2, 3);

        Assert.Null(results[0]);
        Assert.Null(results[1]);                  // 0 missing but held
        Assert.Null(results[2]);
        Assert.Equal((ushort[])[0], results[3]);  // 0 aged out
    }

    [Fact]
    public void A_far_backward_reorder_reports_no_loss()
        // last 1, current 65535 → backward step (distance 65534 ≥ boundary) → not loss, reference not regressed.
        => Assert.All(TrackAll(1, 65535), Assert.Null);
}
