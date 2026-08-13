using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-3, Sendehälfte: Ein Feedback-Paket darf nur dann allein reisen, wenn <c>a=rtcp-rsize</c>
/// ausgehandelt wurde (RFC 5506). Sonst verlangt RFC 3550 §6.1 ein Compound, das mit einem Report
/// beginnt — ein strenger Empfänger darf ein Einzelpaket sonst verwerfen, und das Feedback kommt
/// stillschweigend nie an.
/// </summary>
public sealed class RtcpFeedbackFramingTests
{
    private const uint LocalSsrc = 0x11223344;
    private const uint RemoteSsrc = 0x55667788;

    private static readonly RtcpPacketCodec Codec = new();

    private static RtcpPacket Pli() =>
        new RtcpPictureLossIndication { SenderSsrc = LocalSsrc, MediaSsrc = RemoteSsrc };

    [Fact]
    public void With_reduced_size_negotiated_the_feedback_travels_alone()
    {
        var datagram = RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: true);

        var decoded = Codec.Decode(datagram);
        Assert.Single(decoded);
        Assert.IsType<RtcpPictureLossIndication>(decoded[0]);
    }

    [Fact]
    public void Without_it_the_feedback_is_wrapped_in_a_compound()
    {
        var datagram = RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: false);

        var decoded = Codec.Decode(datagram);
        Assert.Equal(2, decoded.Count);
        Assert.IsType<RtcpReceiverReport>(decoded[0]);   // RFC 3550 §6.1: das Compound beginnt mit einem Report
        Assert.IsType<RtcpPictureLossIndication>(decoded[1]);
    }

    [Fact]
    public void The_prepended_report_identifies_us_and_reports_nothing()
    {
        // RFC 3550 §6.4.2 lässt ein leeres RR (RC=0) ausdrücklich zu — diese Sender führen keine
        // Empfangsstatistik, also wäre jeder Report-Block erfunden.
        var datagram = RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: false);

        var report = Assert.IsType<RtcpReceiverReport>(Codec.Decode(datagram)[0]);
        Assert.Equal(LocalSsrc, report.Ssrc);
        Assert.Empty(report.ReportBlocks);
    }

    [Fact]
    public void The_feedback_itself_is_unchanged_by_the_wrapping()
    {
        var wrapped = Codec.Decode(
            RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: false));
        var alone = Codec.Decode(
            RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: true));

        var wrappedPli = Assert.IsType<RtcpPictureLossIndication>(wrapped[1]);
        var alonePli = Assert.IsType<RtcpPictureLossIndication>(alone[0]);

        Assert.Equal(alonePli.SenderSsrc, wrappedPli.SenderSsrc);
        Assert.Equal(alonePli.MediaSsrc, wrappedPli.MediaSsrc);
    }

    [Fact]
    public void The_compound_form_is_larger_but_still_decodes_as_one_datagram()
    {
        // Der Preis der Compound-Form ist genau der Report davor — nachweisbar, nicht behauptet.
        var reduced = RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: true);
        var compound = RtcpFeedbackFraming.Encode(Codec, Pli(), LocalSsrc, reducedSizeNegotiated: false);

        Assert.True(compound.Length > reduced.Length);
        Assert.Equal(2, Codec.Decode(compound).Count);
    }

    [Fact]
    public void A_null_codec_or_packet_is_refused_rather_than_producing_an_empty_datagram()
    {
        Assert.Throws<ArgumentNullException>(
            () => RtcpFeedbackFraming.Encode(null!, Pli(), LocalSsrc, true));
        Assert.Throws<ArgumentNullException>(
            () => RtcpFeedbackFraming.Encode(Codec, null!, LocalSsrc, true));
    }
}
