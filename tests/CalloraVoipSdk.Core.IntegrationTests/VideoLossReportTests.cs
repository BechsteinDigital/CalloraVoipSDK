using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video arrival-order loss classification (WebRTC phase 3, RFC 4585): the receiver reports
/// loss only on a genuine forward sequence gap. A reorder or duplicate is not loss — it must
/// raise no NACK and no keyframe request, since the reorder window corrects it downstream. A
/// small gap names the missing packets; a large forward loss reports as a PLI-only (empty list).
/// Exercised against the pure classifier so the decision is deterministic (no sockets).
/// </summary>
public sealed class VideoLossReportTests
{
    [Fact]
    public void In_order_arrival_reports_no_loss()
        => Assert.Null(VideoRtpStream.LossReport(100, 101));

    [Fact]
    public void Duplicate_arrival_reports_no_loss()
        => Assert.Null(VideoRtpStream.LossReport(100, 100));

    [Fact]
    public void Backward_reorder_reports_no_loss()
        => Assert.Null(VideoRtpStream.LossReport(100, 98));

    [Fact]
    public void Far_backward_reorder_reports_no_loss()
        => Assert.Null(VideoRtpStream.LossReport(100, 40000));

    [Fact]
    public void Small_forward_gap_names_the_missing_sequence_numbers()
        => Assert.Equal((ushort[])[101, 102], VideoRtpStream.LossReport(100, 103));

    [Fact]
    public void Single_missing_packet_is_named()
        => Assert.Equal((ushort[])[101], VideoRtpStream.LossReport(100, 102));

    [Fact]
    public void Forward_gap_at_the_enumeration_boundary_is_still_named()
    {
        var report = VideoRtpStream.LossReport(100, (ushort)(100 + 256));
        Assert.NotNull(report);
        Assert.Equal(255, report!.Count); // 256-packet gap → 255 missing between
    }

    [Fact]
    public void Forward_loss_beyond_the_enumeration_boundary_reports_pli_only()
    {
        // gap 257 > MaxEnumeratedLoss → empty (signal, but PLI-only), NOT null.
        var report = VideoRtpStream.LossReport(100, (ushort)(100 + 257));
        Assert.NotNull(report);
        Assert.Empty(report!);
    }

    [Fact]
    public void Forward_gap_across_the_wrap_boundary_is_named()
        // last 65535, current 1 → forward distance 2 → the missing packet is 0.
        => Assert.Equal((ushort[])[0], VideoRtpStream.LossReport(65535, 1));

    [Fact]
    public void Reorder_across_the_wrap_boundary_reports_no_loss()
        // last 1, current 65535 → backward step (distance 65534 ≥ boundary) → not loss.
        => Assert.Null(VideoRtpStream.LossReport(1, 65535));
}
