using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The 64-packet sliding replay window shared by SRTP and SRTCP (HARD-R6/R4). It replaces the two
/// byte-identical copies that lived on <c>SrtpSsrcState</c> and <c>SrtcpSsrcState</c>. The window is
/// deliberately <see cref="ulong"/>-keyed: the SRTP extended index reaches 48 bits and must not be
/// truncated to 32 — the large-index test below is the proof.
/// </summary>
public sealed class SlidingReplayWindowTests
{
    private static SlidingReplayWindow NewWindow() => new("index");

    private static void Accept(SlidingReplayWindow w, ulong index)
    {
        w.Check(index);
        w.Update(index);
    }

    [Fact]
    public void Advancing_indices_are_accepted_and_tracked()
    {
        var w = NewWindow();
        Accept(w, 1);
        Accept(w, 2);
        Accept(w, 10);

        Assert.Equal(10UL, w.HighestIndex);
    }

    [Fact]
    public void Exact_replay_is_rejected()
    {
        var w = NewWindow();
        Accept(w, 5);

        Assert.Throws<SrtpReplayException>(() => w.Check(5));
    }

    [Fact]
    public void Old_but_in_window_index_is_accepted_then_rejected_as_replay()
    {
        var w = NewWindow();
        Accept(w, 100);

        // 100 - 40 = 60 < 64 → inside the window, not yet seen → accepted.
        w.Check(60);
        w.Update(60);

        // Now it is a replay.
        Assert.Throws<SrtpReplayException>(() => w.Check(60));
    }

    [Fact]
    public void Index_exactly_window_size_behind_is_outside_the_window()
    {
        var w = NewWindow();
        Accept(w, 100);

        // 100 - 36 = 64 == WindowSize → outside.
        Assert.Throws<SrtpReplayException>(() => w.Check(36));
        // 100 - 37 = 63 < 64 → inside (unseen) → accepted.
        w.Check(37);
    }

    [Fact]
    public void Jump_far_ahead_resets_the_window_so_prior_indices_fall_outside()
    {
        var w = NewWindow();
        Accept(w, 10);
        Accept(w, 1000); // shift >= WindowSize → bitmap reset

        // 10 is now far outside the window.
        Assert.Throws<SrtpReplayException>(() => w.Check(10));
        Assert.Equal(1000UL, w.HighestIndex);
    }

    [Fact]
    public void Large_48_bit_index_is_handled_without_truncation()
    {
        var w = NewWindow();

        // An SRTP extended index above uint.MaxValue: a 32-bit window would truncate this.
        const ulong big = (1UL << 40) + 12345UL;
        Accept(w, big);

        Assert.Equal(big, w.HighestIndex);
        Assert.Throws<SrtpReplayException>(() => w.Check(big));            // replay of the large index
        w.Check(big - 1);                                                 // in-window, unseen → accepted

        // A truncating (uint) window would have mapped `big` to a small value and mis-judged these.
        Assert.True(w.HighestIndex > uint.MaxValue);
    }
}
