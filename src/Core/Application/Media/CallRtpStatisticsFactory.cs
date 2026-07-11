using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Maps the internal <see cref="CallMediaRtpSnapshot"/> runtime record onto the public
/// <see cref="CallRtpStatistics"/> domain value exposed on <see cref="ICall.RtpStatistics"/>.
/// Kept a pure function so the raw-counter exposure has no runtime side effects.
/// </summary>
internal static class CallRtpStatisticsFactory
{
    /// <summary>
    /// Projects <paramref name="snapshot"/> onto the public raw RTP statistics value.
    /// </summary>
    public static CallRtpStatistics From(CallMediaRtpSnapshot snapshot)
        => new(
            CapturedAtUtc: snapshot.CapturedAtUtc,
            LocalSsrc: snapshot.LocalSsrc,
            RemoteSsrc: snapshot.RemoteSsrc,
            PacketsSent: snapshot.SenderPacketCount,
            OctetsSent: snapshot.SenderOctetCount,
            PacketsReceived: snapshot.PacketsReceived,
            PacketsExpected: snapshot.PacketsExpected,
            CumulativePacketsLost: snapshot.CumulativePacketsLost,
            FractionLost: snapshot.FractionLost,
            ExtendedHighestSequenceNumber: snapshot.ExtendedHighestSequenceNumber,
            InterarrivalJitterRtpUnits: snapshot.InterarrivalJitterRtpUnits);
}
