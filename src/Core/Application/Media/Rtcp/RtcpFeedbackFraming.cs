using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

namespace CalloraVoipSdk.Core.Application.Media.Rtcp;

/// <summary>
/// Frames a single RTCP feedback packet into a datagram the peer is allowed to receive
/// (#162 P2-3, RFC 3550 §6.1 / RFC 5506).
/// </summary>
/// <remarks>
/// A feedback packet may travel alone only when <c>a=rtcp-rsize</c> was negotiated. Without it every
/// RTCP datagram has to be a compound that begins with a report — otherwise a strict receiver is
/// entitled to discard it, and the feedback silently never arrives.
///
/// This mirrors what libwebrtc's <c>RTCPSender::PrepareReport</c> does:
/// <code>
/// generate_report = (ConsumeFlag(kRtcpReport) &amp;&amp; method_ == RtcpMode::kReducedSize)
///                   || method_ == RtcpMode::kCompound;
/// if (generate_report) SetFlag(sending_ ? kRtcpSr : kRtcpRr, true);
/// if (IsFlagPresent(kRtcpSr) || (IsFlagPresent(kRtcpRr) &amp;&amp; !cname_.empty())) SetFlag(kRtcpSdes, true);
/// </code>
/// Two details are taken from there deliberately. A report is prepended to <em>every</em> feedback
/// packet in compound mode, not only to periodic ones. And SDES is gated on having a CNAME — libwebrtc
/// sends <c>RR + FB</c> without one rather than refusing, which is what lets this sit at the feedback
/// senders instead of requiring a CNAME threaded down from the session.
///
/// The prepended report carries no report blocks. That is not a shortcut: these senders hold no
/// reception statistics, and RFC 3550 §6.4.2 allows an empty RR (RC=0) precisely for a participant
/// that has nothing to report.
/// </remarks>
internal static class RtcpFeedbackFraming
{
    /// <summary>
    /// Encodes <paramref name="feedback"/> for the wire, adding a leading receiver report when
    /// reduced-size RTCP was not negotiated.
    /// </summary>
    public static byte[] Encode(
        IRtcpPacketCodec codec,
        RtcpPacket feedback,
        uint localSsrc,
        bool reducedSizeNegotiated)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(feedback);

        return reducedSizeNegotiated
            ? codec.Encode([feedback])
            : codec.Encode([EmptyReceiverReport(localSsrc), feedback]);
    }

    private static RtcpReceiverReport EmptyReceiverReport(uint localSsrc) =>
        new() { Ssrc = localSsrc, ReportBlocks = [] };
}
