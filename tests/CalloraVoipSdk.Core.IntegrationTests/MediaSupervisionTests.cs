using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L2 — #261 / ADR-069: media supervision reports silence and, only when a deployment asks for it, ends a
/// call whose peer has stopped sending RTP <em>and</em> RTCP. Supervising RTP alone (the pre-#261 behaviour,
/// 15 s, on by default) tore down live calls: the flake in #256 was our own supervisor doing exactly that
/// while Asterisk never sent a BYE. The teardown is off by default because the interop measurement showed
/// neither reference PBX keeps RTCP flowing during media silence, so it cannot distinguish a quiet peer from
/// a gone one — the application gets <c>MediaFlowChanged</c> and decides.
/// </summary>
public sealed class MediaSupervisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    // The teardown is off by default (#261: measurement showed no PBX keeps RTCP flowing during media
    // silence, so it cannot be told apart from a dead peer). These cases exercise the teardown as a
    // deployment would have to enable it; the shipped defaults are pinned separately below.
    private static readonly MediaSupervisionOptions Defaults =
        MediaSupervisionOptions.Default with { InboundMediaTimeout = TimeSpan.FromSeconds(30) };

    // ── the defect #261 is about ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression guard for #256/#261: 60 seconds of media silence with RTCP still arriving is NOT a
    /// teardown, however far past the liveness timeout it runs. Under the old RTP-only rule this call was
    /// hung up after 15 s.
    /// </summary>
    [Fact]
    public void A_peer_that_reports_rtcp_but_sends_no_media_is_never_hung_up()
    {
        var activity = Supervised();
        Observe(activity, rtp: 100, rtcp: 1, at: T0);

        var verdicts = new List<MediaSupervisionVerdict>();
        for (var second = 5; second <= 60; second += 5)
        {
            // Media stalled at 100 packets; RTCP keeps ticking on the RFC 3550 §6.2 interval.
            var outcome = Observe(activity, rtp: 100, rtcp: 1 + second / 5, at: T0.AddSeconds(second));
            verdicts.Add(outcome.Verdict);
        }

        Assert.DoesNotContain(MediaSupervisionVerdict.PeerGone, verdicts);
        // The application is told once, at the notification threshold — not on every tick.
        Assert.Single(verdicts, v => v == MediaSupervisionVerdict.MediaSilent);
    }

    /// <summary>A peer that stops sending everything is gone, and that ends the call.</summary>
    [Fact]
    public void A_peer_that_stops_sending_rtp_and_rtcp_is_reported_gone()
    {
        var activity = Supervised();
        Observe(activity, rtp: 100, rtcp: 1, at: T0);

        Assert.Equal(MediaSupervisionVerdict.MediaSilent, Observe(activity, 100, 1, T0.AddSeconds(15)).Verdict);
        Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, 100, 1, T0.AddSeconds(29)).Verdict);

        var gone = Observe(activity, rtp: 100, rtcp: 1, at: T0.AddSeconds(30));
        Assert.Equal(MediaSupervisionVerdict.PeerGone, gone.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(30), gone.SilenceDuration);
    }

    /// <summary>The teardown fires at most once, however long the metrics keep arriving afterwards.</summary>
    [Fact]
    public void The_teardown_verdict_is_issued_once()
    {
        var activity = Supervised();
        Observe(activity, rtp: 100, rtcp: 1, at: T0);

        Assert.Equal(MediaSupervisionVerdict.PeerGone, Observe(activity, 100, 1, T0.AddSeconds(30)).Verdict);
        Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, 100, 1, T0.AddSeconds(31)).Verdict);
        Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, 100, 1, T0.AddSeconds(90)).Verdict);
    }

    // ── the notification stage ───────────────────────────────────────────────────────────────

    [Fact]
    public void Media_silence_is_reported_at_the_notify_threshold_and_resumption_afterwards()
    {
        var activity = Supervised();
        Observe(activity, rtp: 100, rtcp: 1, at: T0);

        Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, 100, 2, T0.AddSeconds(14)).Verdict);

        var silent = Observe(activity, rtp: 100, rtcp: 3, at: T0.AddSeconds(15));
        Assert.Equal(MediaSupervisionVerdict.MediaSilent, silent.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(15), silent.SilenceDuration);

        // Reported once, not per tick.
        Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, 100, 4, T0.AddSeconds(20)).Verdict);

        var resumed = Observe(activity, rtp: 150, rtcp: 5, at: T0.AddSeconds(25));
        Assert.Equal(MediaSupervisionVerdict.MediaResumed, resumed.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(25), resumed.SilenceDuration); // the silence that just ended
    }

    /// <summary>Flowing media is silent to the application: no event per metrics tick.</summary>
    [Fact]
    public void Flowing_media_reports_nothing()
    {
        var activity = Supervised();

        for (var second = 0; second <= 60; second += 5)
        {
            var outcome = Observe(activity, rtp: 100 + second * 50, rtcp: 1 + second / 5, at: T0.AddSeconds(second));
            Assert.Equal(MediaSupervisionVerdict.None, outcome.Verdict);
        }
    }

    // ── the exemptions ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A held call carries no inbound media by definition. It is exempt from the teardown by default — as in
    /// SIPSorcery (which skips both local and remote hold) and Asterisk (separate <c>rtp_timeout_hold</c>).
    /// </summary>
    [Fact]
    public void A_held_call_is_not_torn_down_by_default()
    {
        var activity = Supervised();
        Observe(activity, rtp: 100, rtcp: 1, at: T0, state: CallState.Connected);

        var outcome = Observe(activity, rtp: 100, rtcp: 1, at: T0.AddSeconds(120), state: CallState.OnHold);

        Assert.NotEqual(MediaSupervisionVerdict.PeerGone, outcome.Verdict);
    }

    /// <summary>...unless the deployment asks for it.</summary>
    [Fact]
    public void A_held_call_is_torn_down_when_configured()
    {
        var options = Defaults with { HangupHeldCallOnSilence = true };
        var activity = Supervised();
        activity.Observe(Metrics(100, 1), CallState.Connected, options, T0);

        var outcome = activity.Observe(Metrics(100, 1), CallState.OnHold, options, T0.AddSeconds(30));

        Assert.Equal(MediaSupervisionVerdict.PeerGone, outcome.Verdict);
    }

    /// <summary>A call that never received a packet is a negotiation problem, not a supervision one.</summary>
    [Fact]
    public void A_call_that_never_received_media_is_left_alone()
    {
        var activity = Supervised();

        for (var second = 0; second <= 120; second += 10)
            Assert.Equal(MediaSupervisionVerdict.None, Observe(activity, rtp: 0, rtcp: 0, at: T0.AddSeconds(second)).Verdict);
    }

    // ── the configuration contract ───────────────────────────────────────────────────────────

    [Fact]
    public void A_zero_timeout_disables_the_teardown_but_keeps_the_notification()
    {
        var options = Defaults with { InboundMediaTimeout = TimeSpan.Zero };
        var activity = Supervised();
        activity.Observe(Metrics(100, 1), CallState.Connected, options, T0);

        Assert.Equal(
            MediaSupervisionVerdict.MediaSilent,
            activity.Observe(Metrics(100, 1), CallState.Connected, options, T0.AddSeconds(15)).Verdict);
        Assert.Equal(
            MediaSupervisionVerdict.None,
            activity.Observe(Metrics(100, 1), CallState.Connected, options, T0.AddSeconds(600)).Verdict);
    }

    [Fact]
    public void A_zero_notify_delay_disables_the_notification_but_keeps_the_teardown()
    {
        var options = Defaults with { MediaSilenceNotifyAfter = TimeSpan.Zero };
        var activity = Supervised();
        activity.Observe(Metrics(100, 1), CallState.Connected, options, T0);

        Assert.Equal(
            MediaSupervisionVerdict.None,
            activity.Observe(Metrics(100, 1), CallState.Connected, options, T0.AddSeconds(20)).Verdict);
        Assert.Equal(
            MediaSupervisionVerdict.PeerGone,
            activity.Observe(Metrics(100, 1), CallState.Connected, options, T0.AddSeconds(30)).Verdict);
    }

    /// <summary>
    /// The shipped defaults: report media silence, never end the call on it. Measured against both reference
    /// PBXes, inbound RTCP stops together with the media, so an enabled teardown cannot distinguish a quiet
    /// peer from a gone one — Asterisk (<c>rtp_timeout</c>) and FreeSWITCH (<c>media_timeout</c>) ship theirs
    /// off for the same reason, and pjsip has no detection at all.
    /// </summary>
    [Fact]
    public void The_shipped_defaults_notify_and_never_hang_up()
    {
        var shipped = MediaSupervisionOptions.Default;

        Assert.Equal(TimeSpan.Zero, shipped.InboundMediaTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), shipped.MediaSilenceNotifyAfter);
        Assert.False(shipped.HangupHeldCallOnSilence);
    }

    /// <summary>With the shipped defaults, an endless media silence is reported once and never ends the call.</summary>
    [Fact]
    public void With_the_shipped_defaults_endless_silence_never_ends_the_call()
    {
        var shipped = MediaSupervisionOptions.Default;
        var activity = Supervised();
        activity.Observe(Metrics(100, 1), CallState.Connected, shipped, T0);

        var verdicts = new List<MediaSupervisionVerdict>();
        for (var second = 5; second <= 300; second += 5)
            verdicts.Add(activity.Observe(Metrics(100, 1), CallState.Connected, shipped, T0.AddSeconds(second)).Verdict);

        Assert.DoesNotContain(MediaSupervisionVerdict.PeerGone, verdicts);
        Assert.Single(verdicts, v => v == MediaSupervisionVerdict.MediaSilent);
    }

    // ── the public event args ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_media_flow_event_args_carry_the_silence_they_report()
    {
        var args = new CallMediaFlowChangedEventArgs(inboundMediaFlowing: false, TimeSpan.FromSeconds(15), call: null!);

        Assert.False(args.InboundMediaFlowing);
        Assert.Equal(TimeSpan.FromSeconds(15), args.SilenceDuration);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static MediaActivity Supervised() => new() { Call = null!, StartedUtc = T0 };

    private static MediaSupervisionOutcome Observe(
        MediaActivity activity, long rtp, long rtcp, DateTimeOffset at, CallState state = CallState.Connected)
        => activity.Observe(Metrics(rtp, rtcp), state, Defaults, at);

    // Only the two counters the supervision reads carry meaning here; the rest is inert.
    private static CallMediaRuntimeMetrics Metrics(long packetsReceived, long rtcpPacketsReceived) => new(
        capturedAtUtc: T0,
        packetsReceived: packetsReceived,
        packetsQueued: packetsReceived,
        packetsDelivered: packetsReceived,
        packetsDroppedLate: 0,
        packetsDroppedOverflow: 0,
        packetsDroppedDuplicate: 0,
        packetsConcealed: 0,
        packetsUnrecoverableLoss: 0,
        bufferedPackets: 0,
        estimatedJitterMs: 0,
        adaptiveDelayMs: 0,
        estimatedRoundTripTimeMs: 0,
        rtcpPacketsReceived: rtcpPacketsReceived);
}
