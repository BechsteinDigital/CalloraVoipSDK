using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.InteropTests.Soak;

internal sealed record CalloraCapacityDirectionQuality(
    string Direction,
    bool Passed,
    bool ApplicationPassed,
    bool RtpEvidenceAvailable,
    long ExpectedFrames,
    long ApplicationFrames,
    double ApplicationDeliveryRatio,
    uint? RtpPackets,
    double? RtpDeliveryRatio,
    double? RtpCounterWindowSeconds,
    uint? RtpSequenceExpectedPackets,
    long? RtpSequenceGapPackets,
    long? RtpDuplicateOrLatePackets,
    double? PacketLossRatio,
    string PacketLossSource,
    double? RtpInterarrivalJitterMilliseconds,
    DateTimeOffset? RtpCounterWindowStartedAtUtc,
    DateTimeOffset? RtpCounterWindowFinishedAtUtc,
    CalloraCapacityFrameObservation FrameTiming,
    IReadOnlyList<string> Failures);

internal sealed record CalloraCapacityCallQuality(
    int Index,
    string CallId,
    bool Passed,
    bool ApplicationPassed,
    bool RtpEvidenceComplete,
    bool ConnectedThroughoutWindow,
    bool RtcpActiveAtEnd,
    bool RtcpMux,
    long RtcpPacketsSent,
    long RtcpPacketsReceived,
    CalloraCapacityDirectionQuality Outbound,
    CalloraCapacityDirectionQuality Inbound,
    IReadOnlyList<string> Failures);

internal static class CalloraCapacityQualityMeasurement
{
    private const int PcmuClockRate = 8000;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    public static CalloraCapacityCallQuality Create(
        int index,
        ICall call,
        bool connectedThroughout,
        CalloraCapacityFrameObservation outboundFrames,
        CalloraCapacityFrameObservation inboundFrames,
        CallRtpStatistics? rtpBefore,
        CallRtpStatistics? rtpAfter,
        CallQualitySnapshot qualityAfter,
        TimeSpan applicationWindow,
        CalloraCapacityQualityGate gate)
    {
        var applicationExpected = ExpectedFrames(applicationWindow);
        var outbound = CreateOutbound(
            outboundFrames,
            rtpBefore,
            rtpAfter,
            qualityAfter,
            applicationExpected,
            gate);
        var inbound = CreateInbound(
            inboundFrames,
            rtpBefore,
            rtpAfter,
            applicationExpected,
            gate);
        var failures = new List<string>(3);
        if (!connectedThroughout)
        {
            failures.Add("Call was not connected for the complete measurement window.");
        }
        if (!outbound.Passed)
        {
            failures.Add("Outbound direction failed its quality gate.");
        }
        if (!inbound.Passed)
        {
            failures.Add("Inbound direction failed its quality gate.");
        }

        return new CalloraCapacityCallQuality(
            index,
            call.CallId.ToString(),
            failures.Count == 0,
            connectedThroughout && outbound.ApplicationPassed && inbound.ApplicationPassed,
            outbound.RtpEvidenceAvailable && inbound.RtpEvidenceAvailable,
            connectedThroughout,
            qualityAfter.RtcpActive,
            qualityAfter.RtcpMux,
            qualityAfter.RtcpPacketsSent,
            qualityAfter.RtcpPacketsReceived,
            outbound,
            inbound,
            failures);
    }

    private static CalloraCapacityDirectionQuality CreateOutbound(
        CalloraCapacityFrameObservation frames,
        CallRtpStatistics? before,
        CallRtpStatistics? after,
        CallQualitySnapshot qualityAfter,
        long applicationExpected,
        CalloraCapacityQualityGate gate)
    {
        var applicationFailures = ValidateFrameTiming(frames, applicationExpected, gate);
        var failures = new List<string>(applicationFailures);
        uint? rtpPackets = null;
        double? rtpRatio = null;
        double? counterSeconds = null;
        DateTimeOffset? counterStarted = null;
        DateTimeOffset? counterFinished = null;
        var counterAvailable =
            TryCounterWindow(before, after, out var start, out var finish, out var duration);
        if (counterAvailable)
        {
            rtpPackets = CounterDelta(finish.PacketsSent, start.PacketsSent);
            counterSeconds = duration.TotalSeconds;
            counterStarted = start.CapturedAtUtc;
            counterFinished = finish.CapturedAtUtc;
            rtpRatio = Ratio(rtpPackets.Value, ExpectedFrames(duration));
            if (rtpRatio < gate.MinimumDeliveryRatio)
            {
                failures.Add(
                    $"RTP delivery {rtpRatio:P3} was below {gate.MinimumDeliveryRatio:P3}.");
            }
        }
        else
        {
            failures.Add("No advancing RTP counter window was available.");
        }

        double? packetLoss = qualityAfter.RemoteReportPacketLossPercent is { } remoteLoss
            ? remoteLoss / 100d
            : null;
        if (packetLoss is null)
        {
            failures.Add("The peer supplied no outbound RTCP packet-loss report.");
        }
        else if (packetLoss >= gate.MaximumPacketLossRatio)
        {
            failures.Add(
                $"Peer-reported packet loss {packetLoss:P3} was not below " +
                $"{gate.MaximumPacketLossRatio:P3}.");
        }

        var jitter = qualityAfter.RemoteReportJitterMs;
        if (jitter is null)
        {
            failures.Add("The peer supplied no outbound RTCP jitter report.");
        }
        else if (jitter > gate.MaximumJitterMilliseconds)
        {
            failures.Add(
                $"Peer-reported jitter {jitter:F3} ms exceeded " +
                $"{gate.MaximumJitterMilliseconds:F3} ms.");
        }

        return new CalloraCapacityDirectionQuality(
            "Outbound",
            failures.Count == 0,
            applicationFailures.Count == 0,
            counterAvailable && packetLoss.HasValue && jitter.HasValue,
            applicationExpected,
            frames.Frames,
            Ratio(frames.Frames, applicationExpected),
            rtpPackets,
            rtpRatio,
            counterSeconds,
            RtpSequenceExpectedPackets: null,
            RtpSequenceGapPackets: null,
            RtpDuplicateOrLatePackets: null,
            packetLoss,
            "Peer RTCP receiver report (latest reporting interval)",
            jitter,
            counterStarted,
            counterFinished,
            frames,
            failures);
    }

    private static CalloraCapacityDirectionQuality CreateInbound(
        CalloraCapacityFrameObservation frames,
        CallRtpStatistics? before,
        CallRtpStatistics? after,
        long applicationExpected,
        CalloraCapacityQualityGate gate)
    {
        var applicationFailures = ValidateFrameTiming(frames, applicationExpected, gate);
        var failures = new List<string>(applicationFailures);
        uint? rtpPackets = null;
        double? rtpRatio = null;
        double? counterSeconds = null;
        uint? sequenceExpected = null;
        long? sequenceGaps = null;
        long? duplicateOrLate = null;
        double? packetLoss = null;
        double? jitter = null;
        DateTimeOffset? counterStarted = null;
        DateTimeOffset? counterFinished = null;
        var counterAvailable =
            TryCounterWindow(before, after, out var start, out var finish, out var duration);
        if (counterAvailable)
        {
            rtpPackets = CounterDelta(finish.PacketsReceived, start.PacketsReceived);
            sequenceExpected = CounterDelta(finish.PacketsExpected, start.PacketsExpected);
            var signedSequenceDifference = (long)sequenceExpected.Value - rtpPackets.Value;
            sequenceGaps = Math.Max(0, signedSequenceDifference);
            duplicateOrLate = Math.Max(0, -signedSequenceDifference);
            packetLoss = sequenceExpected == 0
                ? (rtpPackets == 0 ? 1 : 0)
                : sequenceGaps.Value / (double)sequenceExpected.Value;
            jitter = finish.InterarrivalJitterRtpUnits * 1000d / PcmuClockRate;
            counterSeconds = duration.TotalSeconds;
            counterStarted = start.CapturedAtUtc;
            counterFinished = finish.CapturedAtUtc;
            rtpRatio = Ratio(rtpPackets.Value, ExpectedFrames(duration));

            if (rtpRatio < gate.MinimumDeliveryRatio)
            {
                failures.Add(
                    $"RTP delivery {rtpRatio:P3} was below {gate.MinimumDeliveryRatio:P3}.");
            }
            if (packetLoss >= gate.MaximumPacketLossRatio)
            {
                failures.Add(
                    $"RTP sequence loss {packetLoss:P3} was not below " +
                    $"{gate.MaximumPacketLossRatio:P3}.");
            }
            if (jitter > gate.MaximumJitterMilliseconds)
            {
                failures.Add(
                    $"RTP interarrival jitter {jitter:F3} ms exceeded " +
                    $"{gate.MaximumJitterMilliseconds:F3} ms.");
            }
        }
        else
        {
            failures.Add("No advancing RTP counter window was available.");
        }

        return new CalloraCapacityDirectionQuality(
            "Inbound",
            failures.Count == 0,
            applicationFailures.Count == 0,
            counterAvailable,
            applicationExpected,
            frames.Frames,
            Ratio(frames.Frames, applicationExpected),
            rtpPackets,
            rtpRatio,
            counterSeconds,
            sequenceExpected,
            sequenceGaps,
            duplicateOrLate,
            packetLoss,
            "Local RFC 3550 extended sequence counters",
            jitter,
            counterStarted,
            counterFinished,
            frames,
            failures);
    }

    private static List<string> ValidateFrameTiming(
        CalloraCapacityFrameObservation frames,
        long expected,
        CalloraCapacityQualityGate gate)
    {
        var failures = new List<string>(6);
        var delivery = Ratio(frames.Frames, expected);
        if (delivery < gate.MinimumDeliveryRatio)
        {
            failures.Add(
                $"Application frame delivery {delivery:P3} was below " +
                $"{gate.MinimumDeliveryRatio:P3}.");
        }
        if (frames.P99IntervalMilliseconds > gate.MaximumP99IntervalMilliseconds)
        {
            failures.Add(
                $"P99 frame interval {frames.P99IntervalMilliseconds:F3} ms exceeded " +
                $"{gate.MaximumP99IntervalMilliseconds:F3} ms.");
        }
        if (frames.MaximumGapMilliseconds >= gate.MaximumSilenceMilliseconds)
        {
            failures.Add(
                $"Maximum edge-inclusive silence {frames.MaximumGapMilliseconds:F3} ms was not below " +
                $"{gate.MaximumSilenceMilliseconds:F3} ms.");
        }

        return failures;
    }

    private static bool TryCounterWindow(
        CallRtpStatistics? before,
        CallRtpStatistics? after,
        out CallRtpStatistics start,
        out CallRtpStatistics finish,
        out TimeSpan duration)
    {
        start = before.GetValueOrDefault();
        finish = after.GetValueOrDefault();
        duration = finish.CapturedAtUtc - start.CapturedAtUtc;
        return before.HasValue && after.HasValue && duration > TimeSpan.Zero;
    }

    private static long ExpectedFrames(TimeSpan duration) =>
        Math.Max(1, (long)Math.Floor(duration.TotalMilliseconds / FrameInterval.TotalMilliseconds));

    private static uint CounterDelta(uint after, uint before) => unchecked(after - before);

    private static double Ratio(long actual, long expected) =>
        expected <= 0 ? 0 : actual / (double)expected;
}
