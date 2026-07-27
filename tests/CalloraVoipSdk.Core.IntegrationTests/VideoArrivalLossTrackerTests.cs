using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14 #6: the stateful arrival-order loss tracker advances to the highest sequence seen and never
/// regresses on a reordered or duplicate packet. Before, the reference tracked the last arrival, so a single
/// forward reorder dragged it backward and made the next in-order packet read as a fresh gap — a spurious
/// NACK/PLI cascade. These pin the reorder-tolerant behaviour end to end (the pure classifier is covered by
/// <see cref="VideoLossReportTests"/>).
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
    public void A_genuine_forward_gap_names_the_missing_packet()
    {
        var results = TrackAll(100, 102);
        Assert.Null(results[0]);
        Assert.Equal((ushort[])[101], results[1]);
    }

    [Fact]
    public void A_forward_reorder_costs_at_most_one_signal()
    {
        // 4 arrives before 3: the gap at 4 names [3] (the reorder window fills it downstream). The later
        // arrival of 3 and the in-order 5 must NOT each produce a fresh signal — before the fix, the
        // reference regressed to 3 on the reorder and 5 then falsely reported [4].
        var results = TrackAll(1, 2, 4, 3, 5);

        Assert.Null(results[0]);                       // 1
        Assert.Null(results[1]);                       // 2
        Assert.Equal((ushort[])[3], results[2]);       // 4 → gap names 3
        Assert.Null(results[3]);                       // 3 (backward reorder) — not loss
        Assert.Null(results[4]);                       // 5 (in-order after highest 4) — no spurious NACK

        Assert.Single(results, r => r is not null);    // exactly one signal for the whole reorder episode
    }

    [Fact]
    public void A_reorder_does_not_regress_the_reference()
    {
        // Highest reaches 105 (naming 101-104), then a stale 102 arrives; the next real packet 106 must be
        // in-order against 105, not a gap against 102.
        var results = TrackAll(100, 105, 102, 106);

        Assert.Null(results[0]);
        Assert.Equal((ushort[])[101, 102, 103, 104], results[1]); // gap at 105
        Assert.Null(results[2]);                                  // stale 102 — backward, not loss
        Assert.Null(results[3]);                                  // 106 is in-order against highest 105
    }

    [Fact]
    public void A_genuine_loss_after_a_reorder_is_still_reported()
    {
        // 4 before 3 (reorder), then a real gap to 7 must still name 5 and 6 against the highest (4).
        var results = TrackAll(2, 4, 3, 7);

        Assert.Null(results[0]);
        Assert.Equal((ushort[])[3], results[1]);            // gap at 4
        Assert.Null(results[2]);                            // reordered 3
        Assert.Equal((ushort[])[5, 6], results[3]);         // real gap 4→7 names 5,6 (highest stayed 4)
    }
}
