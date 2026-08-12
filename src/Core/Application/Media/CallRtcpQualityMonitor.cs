using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
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

    // RFC 3550 §6.2: RTCP bandwidth, conventionally ~5% of the session bandwidth. For a point-to-point leg
    // the Tmin floor dominates, so this only shapes the growth curve; it matches the bundle path's default.
    private const double DefaultRtcpBandwidthBitsPerSecond = 5000.0;
    // §6.3.3: seeds the running average compound size until a real one is measured.
    private const double InitialAverageRtcpSizeBytes = 128.0;

    // Anchor for the default monotonic clock (RTT/DLSR deltas). Mirrors Infrastructure's MonotonicClock, kept
    // local here because the Application layer must not reference Infrastructure (DDD layering); the absolute
    // value is irrelevant since only differences are consumed.
    private static readonly long MonotonicOrigin = Stopwatch.GetTimestamp();

    private readonly ICallMediaSession _mediaSession;
    private readonly IPEndPoint _localRtcpEndPoint;
    private readonly IPEndPoint _remoteRtcpEndPoint;
    private readonly bool _rtcpMux;
    private readonly int _clockRate;
    private readonly string _cname;
    private readonly ILogger<CallRtcpQualityMonitor> _logger;
    private readonly IRtcpPacketCodec _codec;
    private readonly TimeSpan _sendInterval;
    // #162 P2-7: RFC 3550 §6.2/§6.3.1 interval calculation, shared with the bundle path. _sendInterval is
    // kept as the Tmin floor it always was, so an explicitly configured interval still governs the cadence.
    private readonly RtcpTransmissionInterval _intervalCalculator;
    // §6.3.3: running average compound size, seeded until the first report measures a real one.
    private double _averageRtcpSize = InitialAverageRtcpSizeBytes;
    // §6.3.4 sender aging: 1 while we sent RTP during the last reporting interval. "Has ever sent" is not
    // the same question, and answering it that way made every endpoint a permanent sender.
    private int _weSentThisInterval;
    // The sender packet count observed at the previous report; the delta is what decides sender status.
    private uint _lastReportedSenderPacketCount;
    private bool _hasPreviousSenderPacketCount;
    private readonly Func<DateTimeOffset> _monotonicNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();

    private UdpClient? _udp;
    // Handed over from MediaPortReservation (already bound to the RTCP port and held since setup). When
    // present, StartAsync uses it instead of a late bind, closing the "N+1 stolen before StartAsync" race.
    private readonly UdpClient? _preBoundRtcpSocket;
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
    // Monotonic (not wall-clock) instants for the RTT and DLSR deltas (RFC 3550 §6.4.1), so a system-clock step
    // mid-call cannot corrupt them; the wire NTP timestamps and snapshot times stay on the wall clock.
    private DateTimeOffset? _lastLocalSrSentAtMono;
    private uint _lastLocalSrMiddle32;
    private DateTimeOffset? _lastRemoteSrReceivedAtMono;
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
        TimeSpan? sendInterval = null,
        Func<DateTimeOffset>? monotonicNow = null,
        UdpClient? preBoundRtcpSocket = null)
    {
        ArgumentNullException.ThrowIfNull(mediaSession);
        ArgumentNullException.ThrowIfNull(mediaParameters);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _mediaSession = mediaSession;
        _preBoundRtcpSocket = preBoundRtcpSocket;
        _localRtcpEndPoint = ResolveLocalRtcpEndPoint(mediaParameters);
        _remoteRtcpEndPoint = ResolveRemoteRtcpEndPoint(mediaParameters);
        _rtcpMux = mediaParameters.RtcpMux;
        _clockRate = Math.Max(mediaParameters.ClockRate, 1);
        // Opaque per-session CNAME (RFC 7022) — never the machine name (privacy/correlation).
        _cname = RtcpCname.NewOpaque();
        _logger = loggerFactory.CreateLogger<CallRtcpQualityMonitor>();
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sendInterval = sendInterval is { } explicitInterval && explicitInterval > TimeSpan.Zero
            ? explicitInterval
            : DefaultSendInterval;
        _intervalCalculator = new RtcpTransmissionInterval(_sendInterval, DefaultRtcpBandwidthBitsPerSecond);
        _monotonicNow = monotonicNow ?? DefaultMonotonicNow;
        _latestSnapshot = CallQualitySnapshot.CreateEmpty(DateTimeOffset.UtcNow, _rtcpMux);
    }

    // A monotonically non-decreasing instant (RTT/DLSR deltas), immune to wall-clock steps. Injectable via the
    // constructor for deterministic tests; the default is a process-local Stopwatch clock.
    private static DateTimeOffset DefaultMonotonicNow()
        => DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(MonotonicOrigin);

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
            _mediaSession.RtcpCompoundReceived += OnRtcpCompoundReceived;
        }
        else
        {
            try
            {
                // Prefer the pre-bound RTCP socket (reserved as a pair and held since setup) over a late
                // bind: the late bind is exactly what could lose the race for N+1 under concurrent setup.
                _udp = _preBoundRtcpSocket ?? new UdpClient(_localRtcpEndPoint);
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

        _mediaSession.RtcpCompoundReceived -= OnRtcpCompoundReceived;

        // #162 P2-6: announce our departure before tearing the socket down. Until now the SIP path simply
        // went silent, leaving the peer to time this participant out (RFC 3550 §6.3.7) — the bundle path has
        // sent a BYE for a while. Best-effort and bounded: a farewell must never outrank shutting down, and
        // BYE is not retransmitted (§6.6), so a deadline costs at most this one packet. Gated on having
        // reported at least once — a participant the peer never heard from has nothing to depart from.
        await SendGoodbyeAsync().ConfigureAwait(false);

        _cts.Cancel();
        _udp?.Dispose();

        // If StartAsync never adopted the pre-bound RTCP socket into _udp — disposed before start, or
        // rtcp-mux was negotiated so the separate RTCP socket was never used — it is still open and would
        // hold its reserved port until GC. Close it deterministically. ReferenceEquals avoids a
        // double-dispose when it was adopted (then _udp already disposed it above).
        if (!ReferenceEquals(_udp, _preBoundRtcpSocket))
            _preBoundRtcpSocket?.Dispose();

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
            // #162 P2-7: RFC 3550 §6.2/§6.3.1 scheduling instead of a fixed PeriodicTimer. A rigid interval
            // makes every endpoint report on the same cadence, which is exactly what the RFC's randomisation
            // exists to prevent; the bundle path has computed its interval this way for a while. The first
            // report uses half Tmin (§6.2), so the loop delays before it — the old immediate send skipped
            // that deliberate stagger entirely.
            var interval = _intervalCalculator.Compute(
                members: 2, senders: 1, weSent: false, averageRtcpSizeBytes: InitialAverageRtcpSizeBytes, initial: true);

            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var sizeBytes = await SendReportAsync(cancellationToken).ConfigureAwait(false);

                // §6.3.3: fold the observed compound size into the running average, so the interval tracks
                // what we actually put on the wire rather than a constant.
                if (sizeBytes > 0)
                    _averageRtcpSize += (sizeBytes - _averageRtcpSize) / 16.0;

                // A point-to-point leg: us and the peer. Whether we count as a sender is decided per report
                // by the sender-aging check, not by "has ever sent".
                interval = _intervalCalculator.Compute(
                    members: 2,
                    senders: Volatile.Read(ref _weSentThisInterval) != 0 ? 1 : 0,
                    weSent: Volatile.Read(ref _weSentThisInterval) != 0,
                    averageRtcpSizeBytes: _averageRtcpSize,
                    initial: false);
            }
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

                HandleInboundDatagram(received.Buffer, DateTimeOffset.UtcNow, _monotonicNow());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTCP receive loop failed unexpectedly.");
        }
    }

    // Bound on the best-effort teardown BYE (#162 P2-6), matching the bundle path's.
    private static readonly TimeSpan GoodbyeSendDeadline = TimeSpan.FromMilliseconds(500);

    // RFC 3550 §6.6: leaving without a BYE is legal but leaves the peer to time us out. §6.1 requires the
    // compound to lead with a report and carry a CNAME, so the farewell is RR + SDES + BYE for our own SSRC —
    // the same shape the bundle path sends, and self-consistent: the SSRC that departs is the one the SDES
    // identifies.
    private async ValueTask SendGoodbyeAsync()
    {
        if (Volatile.Read(ref _rtcpPacketsSent) == 0)
            return;   // never announced ourselves; nothing to depart from

        try
        {
            var rtpSnapshot = _mediaSession.GetRtpSnapshot();
            var packets = new RtcpPacket[]
            {
                new RtcpReceiverReport { Ssrc = rtpSnapshot.LocalSsrc, ReportBlocks = [] },
                new RtcpSdesPacket
                {
                    Chunks =
                    [
                        new RtcpSdesChunk
                        {
                            Ssrc = rtpSnapshot.LocalSsrc,
                            Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = _cname }],
                        },
                    ],
                },
                new RtcpByePacket { Sources = [rtpSnapshot.LocalSsrc], Reason = "leaving" },
            };

            var datagram = _codec.Encode(packets);
            using var deadline = new CancellationTokenSource(GoodbyeSendDeadline);

            if (_rtcpMux)
                await _mediaSession.SendRtcpMuxDatagramAsync(datagram, deadline.Token).ConfigureAwait(false);
            else if (_udp is not null)
                await _udp.SendAsync(datagram, _remoteRtcpEndPoint, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("RTCP BYE on teardown timed out after {Deadline}; continuing shutdown.", GoodbyeSendDeadline);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RTCP BYE on teardown could not be sent (best-effort).");
        }
    }

    // Returns the encoded compound size in bytes, or 0 when nothing reached the wire, so the caller can
    // fold it into the RFC 3550 §6.3.3 running average.
    private async Task<int> SendReportAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var monotonicNow = _monotonicNow();
        var rtpSnapshot = _mediaSession.GetRtpSnapshot();

        // #162 P2-7 sender aging (RFC 3550 §6.3.4/§6.4): "we sent" means RTP went out during THIS reporting
        // interval, not that the endpoint ever sent. Using HasSentRtpPackets made a leg that spoke once emit
        // Sender Reports forever — inflating the peer's sender count, skewing its bandwidth split, and
        // describing a stream that stopped. The packet counter is the reliable signal and needs no extra
        // plumbing: no increment since the previous report means no RTP was sent in between.
        var weSent = _hasPreviousSenderPacketCount
            ? rtpSnapshot.SenderPacketCount != _lastReportedSenderPacketCount
            : rtpSnapshot.HasSentRtpPackets;
        _lastReportedSenderPacketCount = rtpSnapshot.SenderPacketCount;
        _hasPreviousSenderPacketCount = true;
        Volatile.Write(ref _weSentThisInterval, weSent ? 1 : 0);

        var packets = BuildCompoundReport(rtpSnapshot, now, monotonicNow, weSent, out var localSrMiddle32, out var sentSenderReport);
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
                return 0;
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
            return 0;
        }

        Interlocked.Increment(ref _rtcpPacketsSent);

        if (sentSenderReport)
        {
            lock (_sync)
            {
                _lastLocalSrMiddle32 = localSrMiddle32;
                _lastLocalSrSentAtMono = monotonicNow;
            }
        }

        PublishSnapshot(rtpSnapshot, now, rtcpActive: true);
        return datagram.Length;
    }

    private IReadOnlyList<RtcpPacket> BuildCompoundReport(
        CallMediaRtpSnapshot rtpSnapshot,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset capturedAtMono,
        bool weSent,
        out uint localSrMiddle32,
        out bool sentSenderReport)
    {
        // DLSR is a delay (a delta against the monotonic instant we received the remote SR); the wire NTP
        // timestamp below stays wall-clock so peers read a real time-of-day (RFC 3550 §6.4.1).
        var reportBlocks = BuildReportBlocks(rtpSnapshot, capturedAtMono);
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

        // RFC 3550 §6.4: a Sender Report is for a participant that sent RTP during this interval; everyone
        // else sends a Receiver Report. `weSent` carries the aged answer (#162 P2-7).
        if (weSent)
        {
            var ntp = ToNtpTimestamp(capturedAtUtc);
            localSrMiddle32 = ToMiddle32Bits(ntp);
            sentSenderReport = true;

            var senderReport = new RtcpSenderReport
            {
                Ssrc = rtpSnapshot.LocalSsrc,
                NtpTimestamp = ntp,
                RtpTimestamp = ExtrapolateSenderReportRtpTimestamp(
                    rtpSnapshot.LastSentRtpTimestamp, rtpSnapshot.SinceLastRtpSend, _clockRate),
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

    // RFC 3550 §6.4.1: the SR's RTP timestamp must correspond to the same instant as its NTP timestamp — the
    // report instant, NOT the last packet sent. Pairing current NTP with the last sent RTP timestamp makes the
    // NTP↔RTP mapping stale during a send pause or DTX, and that mapping is what the peer uses to compute
    // inter-media synchronisation. Project the last sent timestamp forward onto the report instant on this
    // stream's clock (#162 P2-8; the bundle path already did this):
    //   srRtpTs = lastRtpTs + round(sinceLastSend × clockRate)
    // The add wraps as RTP timestamps do (§5.1). Falls back to the raw last timestamp when the sender reported
    // no elapsed span — never worse than the previous behaviour.
    internal static uint ExtrapolateSenderReportRtpTimestamp(
        uint lastSentRtpTimestamp, TimeSpan? sinceLastSend, int clockRate)
    {
        if (sinceLastSend is not { } elapsed || elapsed <= TimeSpan.Zero || clockRate <= 0)
            return lastSentRtpTimestamp;

        var advance = (long)Math.Round(elapsed.TotalSeconds * clockRate, MidpointRounding.AwayFromZero);
        return unchecked(lastSentRtpTimestamp + (uint)advance);
    }

    private IReadOnlyList<RtcpReportBlock> BuildReportBlocks(CallMediaRtpSnapshot rtpSnapshot, DateTimeOffset nowMono)
    {
        if (rtpSnapshot.RemoteSsrc is not { } remoteSsrc)
            return [];

        uint lsr;
        uint dlsr;
        lock (_sync)
        {
            lsr = _lastRemoteSrMiddle32;
            dlsr = _lastRemoteSrReceivedAtMono is { } receivedAt
                ? ToDlsr(nowMono - receivedAt)
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

    // The session decodes each inbound RTCP compound once and hands us the shared, read-only packet list
    // (RtcpCompoundReceived) — no per-consumer re-parse of the same bytes.
    private void OnRtcpCompoundReceived(IReadOnlyList<RtcpPacket> packets)
        => HandleRtcpPackets(packets, DateTimeOffset.UtcNow, _monotonicNow());

    /// <summary>
    /// Test seam: processes one inbound RTCP datagram as if received off the wire, with the same instant used as
    /// both the wall-clock (snapshot) and monotonic (RTT) capture time.
    /// </summary>
    internal void ProcessInboundDatagramForTest(byte[] datagram, DateTimeOffset capturedAt)
        => ProcessInboundDatagramForTest(datagram, capturedAt, capturedAt);

    /// <summary>
    /// Test seam: processes one inbound RTCP datagram with distinct wall-clock and monotonic capture instants,
    /// so a test can prove the RTT is derived from the monotonic clock and not the wall clock. Decodes the bytes
    /// (the production path receives the compound already decoded via <see cref="OnRtcpCompoundReceived"/>).
    /// </summary>
    internal void ProcessInboundDatagramForTest(byte[] datagram, DateTimeOffset capturedAtUtc, DateTimeOffset capturedAtMono)
        => HandleInboundDatagram(datagram, capturedAtUtc, capturedAtMono);

    // Non-RTCP-MUX path (a dedicated RTCP socket) and the test seams: decode a raw datagram here, then dispatch.
    // In RTCP-MUX mode the session decodes once and delivers via OnRtcpCompoundReceived, so no decode happens here.
    private void HandleInboundDatagram(byte[] datagram, DateTimeOffset capturedAtUtc, DateTimeOffset capturedAtMono)
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

        HandleRtcpPackets(packets, capturedAtUtc, capturedAtMono);
    }

    /// <summary>Test seam: records the local sender-report state normally set by the send loop.</summary>
    internal void RecordLocalSenderReportForTest(DateTimeOffset sentAtMono, uint ntpMiddle32)
    {
        lock (_sync)
        {
            _lastLocalSrSentAtMono = sentAtMono;
            _lastLocalSrMiddle32 = ntpMiddle32;
        }
    }

    private void HandleRtcpPackets(IReadOnlyList<RtcpPacket> packets, DateTimeOffset capturedAtUtc, DateTimeOffset capturedAtMono)
    {
        Interlocked.Increment(ref _rtcpPacketsReceived);
        var rtpSnapshot = _mediaSession.GetRtpSnapshot();

        foreach (var packet in packets)
        {
            switch (packet)
            {
                case RtcpSenderReport senderReport:
                    HandleSenderReport(senderReport, rtpSnapshot.LocalSsrc, capturedAtMono);
                    break;

                case RtcpReceiverReport receiverReport:
                    HandleReceiverReport(receiverReport, rtpSnapshot.LocalSsrc, capturedAtMono);
                    break;

                case RtcpExtendedReport extendedReport:
                    HandleExtendedReport(extendedReport, rtpSnapshot.LocalSsrc);
                    break;
            }
        }

        PublishSnapshot(rtpSnapshot, capturedAtUtc, rtcpActive: true);
    }

    private void HandleSenderReport(RtcpSenderReport senderReport, uint localSsrc, DateTimeOffset capturedAtMono)
    {
        lock (_sync)
        {
            _lastRemoteSrMiddle32 = ToMiddle32Bits(senderReport.NtpTimestamp);
            _lastRemoteSrReceivedAtMono = capturedAtMono;
        }

        UpdateRemoteQualityMetrics(senderReport.ReportBlocks, localSsrc, capturedAtMono);
    }

    private void HandleReceiverReport(RtcpReceiverReport receiverReport, uint localSsrc, DateTimeOffset capturedAtMono)
        => UpdateRemoteQualityMetrics(receiverReport.ReportBlocks, localSsrc, capturedAtMono);

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
        DateTimeOffset capturedAtMono)
    {
        var block = blocks.FirstOrDefault(b => b.Ssrc == localSsrc);
        if (block is null)
            return;

        var remoteJitterMs = block.Jitter * 1000.0 / _clockRate;
        var remoteLossPercent = block.FractionLost * 100.0 / 256.0;
        double? roundTripTimeMs = null;

        DateTimeOffset? lastLocalSrSentAtMono = null;
        uint expectedLastSr = 0;
        lock (_sync)
        {
            if (_lastLocalSrSentAtMono.HasValue)
            {
                lastLocalSrSentAtMono = _lastLocalSrSentAtMono.Value;
                expectedLastSr = _lastLocalSrMiddle32;
            }
        }

        if (block.LastSr != 0 &&
            lastLocalSrSentAtMono.HasValue &&
            block.LastSr == expectedLastSr)
        {
            var dlsr = TimeSpan.FromSeconds(block.DelaySinceLastSr / 65536.0);
            // Monotonic delta (RFC 3550 §6.4.1): both instants come from _monotonicNow, so a wall-clock step
            // between sending our SR and this report arriving cannot corrupt the RTT.
            var computedRtt = capturedAtMono - lastLocalSrSentAtMono.Value - dlsr;
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
