using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Application-level RTCP runtime component for one active call media session.
/// This service is responsible for wiring RTP runtime counters to RTCP SR/RR,
/// receiving inbound RTCP reports, and publishing public call quality snapshots.
/// </summary>
internal sealed class CallRtcpQualityMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultSendInterval = TimeSpan.FromSeconds(5);

    private readonly ICallMediaSession _mediaSession;
    private readonly IPEndPoint _localRtcpEndPoint;
    private readonly IPEndPoint _remoteRtcpEndPoint;
    private readonly bool _rtcpMux;
    private readonly int _clockRate;
    private readonly string _cname;
    private readonly ILogger<CallRtcpQualityMonitor> _logger;
    private readonly IRtcpPacketCodec _codec;
    private readonly TimeSpan _sendInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private UdpClient? _udp;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private double? _remoteReportJitterMs;
    private double? _remoteReportLossPercent;
    private double? _roundTripTimeMs;
    private double? _remoteMosLq;
    private double? _remoteMosCq;
    private long _rtcpPacketsSent;
    private long _rtcpPacketsReceived;
    private CallQualitySnapshot _latestSnapshot;
    private CallMediaRtpSnapshot _latestRtpSnapshot;
    private bool _hasRtpSnapshot;
    private DateTimeOffset? _lastLocalSrSentAtUtc;
    private uint _lastLocalSrMiddle32;
    private DateTimeOffset? _lastRemoteSrReceivedAtUtc;
    private uint _lastRemoteSrMiddle32;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Raised whenever a new quality snapshot is published.
    /// </summary>
    internal event Action<CallQualitySnapshot>? QualitySnapshotUpdated;

    /// <summary>
    /// Creates a monitor for one call media session.
    /// </summary>
    internal CallRtcpQualityMonitor(
        ICallMediaSession mediaSession,
        CallMediaParameters mediaParameters,
        ILoggerFactory loggerFactory,
        IRtcpPacketCodec codec,
        TimeSpan? sendInterval = null)
    {
        ArgumentNullException.ThrowIfNull(mediaSession);
        ArgumentNullException.ThrowIfNull(mediaParameters);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _mediaSession = mediaSession;
        _localRtcpEndPoint = ResolveLocalRtcpEndPoint(mediaParameters);
        _remoteRtcpEndPoint = ResolveRemoteRtcpEndPoint(mediaParameters);
        _rtcpMux = mediaParameters.RtcpMux;
        _clockRate = Math.Max(mediaParameters.ClockRate, 1);
        _cname = $"voipsdk-{Environment.MachineName}";
        _logger = loggerFactory.CreateLogger<CallRtcpQualityMonitor>();
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sendInterval = sendInterval is { } explicitInterval && explicitInterval > TimeSpan.Zero
            ? explicitInterval
            : DefaultSendInterval;
        _latestSnapshot = CallQualitySnapshot.CreateEmpty(DateTimeOffset.UtcNow, _rtcpMux);
    }

    /// <summary>
    /// Starts RTCP sender/receiver loops.
    /// </summary>
    internal Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return Task.CompletedTask;

        if (_rtcpMux)
        {
            _mediaSession.RtcpMuxDatagramReceived += OnRtcpMuxDatagramReceived;
        }
        else
        {
            try
            {
                _udp = new UdpClient(_localRtcpEndPoint);
                _receiveLoop = RunReceiveLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to bind RTCP socket on {LocalRtcpEndPoint}; quality reporting is disabled.",
                    _localRtcpEndPoint);
                PublishSnapshot(_mediaSession.GetRtpSnapshot(), DateTimeOffset.UtcNow, rtcpActive: false);
                return Task.CompletedTask;
            }
        }

        _sendLoop = RunSendLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the most recently published quality snapshot.
    /// </summary>
    internal CallQualitySnapshot GetLatestSnapshot()
    {
        lock (_sync)
        {
            return _latestSnapshot;
        }
    }

    /// <summary>
    /// Returns the most recently captured raw RTP snapshot, or <see langword="null"/> before the
    /// first RTCP reporting interval has produced counters.
    /// </summary>
    internal CallMediaRtpSnapshot? GetLatestRtpSnapshot()
    {
        lock (_sync)
        {
            return _hasRtpSnapshot ? _latestRtpSnapshot : null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _mediaSession.RtcpMuxDatagramReceived -= OnRtcpMuxDatagramReceived;
        _cts.Cancel();
        _udp?.Dispose();

        if (_sendLoop is not null)
        {
            try
            {
                await _sendLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTCP send loop terminated with an error during dispose.");
            }
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTCP receive loop terminated with an error during dispose.");
            }
        }

        lock (_sync)
        {
            QualitySnapshotUpdated = null;
        }

        _cts.Dispose();
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendReportAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_sendInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SendReportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTCP send loop failed unexpectedly.");
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_udp is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        ex,
                        "RTCP socket error on {LocalRtcpEndPoint}.",
                        _localRtcpEndPoint);
                    continue;
                }

                HandleInboundDatagram(received.Buffer, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTCP receive loop failed unexpectedly.");
        }
    }

    private async Task SendReportAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rtpSnapshot = _mediaSession.GetRtpSnapshot();
        var packets = BuildCompoundReport(rtpSnapshot, now, out var localSrMiddle32, out var sentSenderReport);
        var datagram = _codec.Encode(packets);

        try
        {
            if (_rtcpMux)
            {
                await _mediaSession.SendRtcpMuxDatagramAsync(datagram, cancellationToken).ConfigureAwait(false);
            }
            else if (_udp is not null)
            {
                await _udp.SendAsync(datagram, _remoteRtcpEndPoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                PublishSnapshot(rtpSnapshot, now, rtcpActive: false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed sending RTCP report to {RemoteRtcpEndPoint}.",
                _remoteRtcpEndPoint);
            PublishSnapshot(rtpSnapshot, now, rtcpActive: false);
            return;
        }

        Interlocked.Increment(ref _rtcpPacketsSent);

        if (sentSenderReport)
        {
            lock (_sync)
            {
                _lastLocalSrMiddle32 = localSrMiddle32;
                _lastLocalSrSentAtUtc = now;
            }
        }

        PublishSnapshot(rtpSnapshot, now, rtcpActive: true);
    }

    private IReadOnlyList<RtcpPacket> BuildCompoundReport(
        CallMediaRtpSnapshot rtpSnapshot,
        DateTimeOffset capturedAtUtc,
        out uint localSrMiddle32,
        out bool sentSenderReport)
    {
        var reportBlocks = BuildReportBlocks(rtpSnapshot, capturedAtUtc);
        var sdes = new RtcpSdesPacket
        {
            Chunks =
            [
                new RtcpSdesChunk
                {
                    Ssrc = rtpSnapshot.LocalSsrc,
                    Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = _cname }]
                }
            ]
        };

        if (rtpSnapshot.HasSentRtpPackets)
        {
            var ntp = ToNtpTimestamp(capturedAtUtc);
            localSrMiddle32 = ToMiddle32Bits(ntp);
            sentSenderReport = true;

            var senderReport = new RtcpSenderReport
            {
                Ssrc = rtpSnapshot.LocalSsrc,
                NtpTimestamp = ntp,
                RtpTimestamp = rtpSnapshot.LastSentRtpTimestamp,
                SenderPacketCount = rtpSnapshot.SenderPacketCount,
                SenderOctetCount = rtpSnapshot.SenderOctetCount,
                ReportBlocks = reportBlocks
            };

            return [senderReport, sdes];
        }

        localSrMiddle32 = 0;
        sentSenderReport = false;
        var receiverReport = new RtcpReceiverReport
        {
            Ssrc = rtpSnapshot.LocalSsrc,
            ReportBlocks = reportBlocks
        };
        return [receiverReport, sdes];
    }

    private IReadOnlyList<RtcpReportBlock> BuildReportBlocks(CallMediaRtpSnapshot rtpSnapshot, DateTimeOffset capturedAtUtc)
    {
        if (rtpSnapshot.RemoteSsrc is not { } remoteSsrc)
            return [];

        uint lsr;
        uint dlsr;
        lock (_sync)
        {
            lsr = _lastRemoteSrMiddle32;
            dlsr = _lastRemoteSrReceivedAtUtc is { } receivedAt
                ? ToDlsr(capturedAtUtc - receivedAt)
                : 0;
        }

        var block = new RtcpReportBlock
        {
            Ssrc = remoteSsrc,
            FractionLost = rtpSnapshot.FractionLost,
            CumulativePacketsLost = rtpSnapshot.CumulativePacketsLost,
            ExtendedHighestSeq = rtpSnapshot.ExtendedHighestSequenceNumber,
            Jitter = rtpSnapshot.InterarrivalJitterRtpUnits,
            LastSr = lsr,
            DelaySinceLastSr = dlsr
        };

        return [block];
    }

    private void OnRtcpMuxDatagramReceived(byte[] datagram)
        => HandleInboundDatagram(datagram, DateTimeOffset.UtcNow);

    /// <summary>Test seam: processes one inbound RTCP datagram as if received off the wire.</summary>
    internal void ProcessInboundDatagramForTest(byte[] datagram, DateTimeOffset capturedAtUtc)
        => HandleInboundDatagram(datagram, capturedAtUtc);

    /// <summary>Test seam: records the local sender-report state normally set by the send loop.</summary>
    internal void RecordLocalSenderReportForTest(DateTimeOffset sentAtUtc, uint ntpMiddle32)
    {
        lock (_sync)
        {
            _lastLocalSrSentAtUtc = sentAtUtc;
            _lastLocalSrMiddle32 = ntpMiddle32;
        }
    }

    private void HandleInboundDatagram(byte[] datagram, DateTimeOffset capturedAtUtc)
    {
        if (datagram.Length == 0)
            return;

        IReadOnlyList<RtcpPacket> packets;
        try
        {
            packets = _codec.Decode(datagram);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Ignoring invalid inbound RTCP datagram.");
            return;
        }

        Interlocked.Increment(ref _rtcpPacketsReceived);
        var rtpSnapshot = _mediaSession.GetRtpSnapshot();

        foreach (var packet in packets)
        {
            switch (packet)
            {
                case RtcpSenderReport senderReport:
                    HandleSenderReport(senderReport, rtpSnapshot.LocalSsrc, capturedAtUtc);
                    break;

                case RtcpReceiverReport receiverReport:
                    HandleReceiverReport(receiverReport, rtpSnapshot.LocalSsrc, capturedAtUtc);
                    break;

                case RtcpExtendedReport extendedReport:
                    HandleExtendedReport(extendedReport, rtpSnapshot.LocalSsrc);
                    break;
            }
        }

        PublishSnapshot(rtpSnapshot, capturedAtUtc, rtcpActive: true);
    }

    private void HandleSenderReport(RtcpSenderReport senderReport, uint localSsrc, DateTimeOffset capturedAtUtc)
    {
        lock (_sync)
        {
            _lastRemoteSrMiddle32 = ToMiddle32Bits(senderReport.NtpTimestamp);
            _lastRemoteSrReceivedAtUtc = capturedAtUtc;
        }

        UpdateRemoteQualityMetrics(senderReport.ReportBlocks, localSsrc, capturedAtUtc);
    }

    private void HandleReceiverReport(RtcpReceiverReport receiverReport, uint localSsrc, DateTimeOffset capturedAtUtc)
        => UpdateRemoteQualityMetrics(receiverReport.ReportBlocks, localSsrc, capturedAtUtc);

    private void HandleExtendedReport(RtcpExtendedReport report, uint localSsrc)
    {
        // The peer's VoIP Metrics block reports on the stream it received from us, so it is keyed by
        // our SSRC (RFC 3611 §4.7). Surface the peer's listening/conversational MOS scores.
        var metrics = report.VoipMetrics.FirstOrDefault(b => b.SourceSsrc == localSsrc);
        if (metrics is null)
            return;

        lock (_sync)
        {
            _remoteMosLq = MosFromByte(metrics.MosLq);
            _remoteMosCq = MosFromByte(metrics.MosCq);
        }
    }

    // RFC 3611 §4.7: MOS is carried as the score ×10 (valid 10–50); 0 and 127 mean unavailable.
    private static double? MosFromByte(byte mosTimesTen)
        => mosTimesTen is 0 or 127 ? null : mosTimesTen / 10.0;

    private void UpdateRemoteQualityMetrics(
        IReadOnlyList<RtcpReportBlock> blocks,
        uint localSsrc,
        DateTimeOffset capturedAtUtc)
    {
        var block = blocks.FirstOrDefault(b => b.Ssrc == localSsrc);
        if (block is null)
            return;

        var remoteJitterMs = block.Jitter * 1000.0 / _clockRate;
        var remoteLossPercent = block.FractionLost * 100.0 / 256.0;
        double? roundTripTimeMs = null;

        DateTimeOffset? lastLocalSrSentAtUtc = null;
        uint expectedLastSr = 0;
        lock (_sync)
        {
            if (_lastLocalSrSentAtUtc.HasValue)
            {
                lastLocalSrSentAtUtc = _lastLocalSrSentAtUtc.Value;
                expectedLastSr = _lastLocalSrMiddle32;
            }
        }

        if (block.LastSr != 0 &&
            lastLocalSrSentAtUtc.HasValue &&
            block.LastSr == expectedLastSr)
        {
            var dlsr = TimeSpan.FromSeconds(block.DelaySinceLastSr / 65536.0);
            var computedRtt = capturedAtUtc - lastLocalSrSentAtUtc.Value - dlsr;
            if (computedRtt > TimeSpan.Zero)
                roundTripTimeMs = computedRtt.TotalMilliseconds;
        }

        lock (_sync)
        {
            _remoteReportJitterMs = remoteJitterMs;
            _remoteReportLossPercent = remoteLossPercent;
            if (roundTripTimeMs.HasValue)
                _roundTripTimeMs = roundTripTimeMs.Value;
        }

        // Feed the measured RTT into the adaptive jitter buffer (outside the lock).
        // Without this the buffer keeps its InitialRoundTripTimeMs default forever and
        // media metrics report a configuration constant as if it were a measurement.
        if (roundTripTimeMs.HasValue)
            _mediaSession.UpdateRoundTripTimeHint(TimeSpan.FromMilliseconds(roundTripTimeMs.Value));
    }

    private void PublishSnapshot(CallMediaRtpSnapshot rtpSnapshot, DateTimeOffset capturedAtUtc, bool rtcpActive)
    {
        var snapshot = CreateSnapshot(rtpSnapshot, capturedAtUtc, rtcpActive);
        lock (_sync)
        {
            _latestSnapshot = snapshot;
            _latestRtpSnapshot = rtpSnapshot;
            _hasRtpSnapshot = true;
        }

        try
        {
            QualitySnapshotUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching call quality snapshot.");
        }
    }

    private CallQualitySnapshot CreateSnapshot(
        CallMediaRtpSnapshot rtpSnapshot,
        DateTimeOffset capturedAtUtc,
        bool rtcpActive)
    {
        double? remoteJitterMs;
        double? remoteLossPercent;
        double? roundTripTimeMs;
        double? remoteMosLq;
        double? remoteMosCq;
        lock (_sync)
        {
            remoteJitterMs = _remoteReportJitterMs;
            remoteLossPercent = _remoteReportLossPercent;
            roundTripTimeMs = _roundTripTimeMs;
            remoteMosLq = _remoteMosLq;
            remoteMosCq = _remoteMosCq;
        }

        return new CallQualitySnapshot(
            CapturedAtUtc: capturedAtUtc,
            RtcpActive: rtcpActive,
            RtcpMux: _rtcpMux,
            LocalReceiveJitterMs: rtpSnapshot.LocalReceiveJitterMs,
            LocalReceivePacketLossPercent: rtpSnapshot.LocalReceivePacketLossPercent,
            RemoteReportJitterMs: remoteJitterMs,
            RemoteReportPacketLossPercent: remoteLossPercent,
            RoundTripTimeMs: roundTripTimeMs,
            RtcpPacketsSent: Interlocked.Read(ref _rtcpPacketsSent),
            RtcpPacketsReceived: Interlocked.Read(ref _rtcpPacketsReceived),
            RemoteMosListeningQuality: remoteMosLq,
            RemoteMosConversationalQuality: remoteMosCq);
    }

    private static IPEndPoint ResolveLocalRtcpEndPoint(CallMediaParameters mediaParameters)
    {
        if (mediaParameters.LocalRtcpEndPoint is not null)
            return mediaParameters.LocalRtcpEndPoint;

        var port = mediaParameters.RtcpMux
            ? mediaParameters.LocalEndPoint.Port
            : checked(mediaParameters.LocalEndPoint.Port + 1);
        return new IPEndPoint(mediaParameters.LocalEndPoint.Address, port);
    }

    private static IPEndPoint ResolveRemoteRtcpEndPoint(CallMediaParameters mediaParameters)
    {
        if (mediaParameters.RemoteRtcpEndPoint is not null)
            return mediaParameters.RemoteRtcpEndPoint;

        var port = mediaParameters.RtcpMux
            ? mediaParameters.RemoteEndPoint.Port
            : checked(mediaParameters.RemoteEndPoint.Port + 1);
        return new IPEndPoint(mediaParameters.RemoteEndPoint.Address, port);
    }

    private static ulong ToNtpTimestamp(DateTimeOffset timestamp)
    {
        var ntpEpoch = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var delta = timestamp.ToUniversalTime() - ntpEpoch;
        var totalSeconds = Math.Max(0, delta.TotalSeconds);
        var wholeSeconds = Math.Floor(totalSeconds);
        var seconds = (ulong)wholeSeconds;
        var fraction = (ulong)((totalSeconds - wholeSeconds) * 4_294_967_296.0);
        return (seconds << 32) | fraction;
    }

    private static uint ToMiddle32Bits(ulong ntpTimestamp)
        => (uint)((ntpTimestamp >> 16) & 0xFFFFFFFF);

    private static uint ToDlsr(TimeSpan elapsedSinceLastSr)
    {
        if (elapsedSinceLastSr <= TimeSpan.Zero)
            return 0;

        var value = elapsedSinceLastSr.TotalSeconds * 65536.0;
        if (value >= uint.MaxValue)
            return uint.MaxValue;

        return (uint)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
