using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Periodically emits RTCP reports for a BUNDLE media session (RFC 3550 §6.4). A background loop, every
/// <c>interval</c>, snapshots each sending track's SR counters
/// (<see cref="BundledOutboundPipeline.SnapshotSenderReports"/>) and the per-SSRC inbound reception
/// statistics (<see cref="BundledInboundReceptionStats.SnapshotReportBlocks"/>), then builds one compound
/// RTCP packet and sends it via the outbound pipeline's fail-closed SRTCP send path.
/// <para>
/// The compound's first packet carries the reception report blocks (one per active inbound SSRC:
/// fraction-lost, cumulative-lost, extended-highest-seq, interarrival jitter, LSR, DLSR — RFC 3550 §6.4.1):
/// when this endpoint is sending, a <see cref="RtcpSenderReport"/> per sending SSRC carries those blocks
/// (the first SR carries them all, distributing across SRs if the count exceeds the 31-block SR limit);
/// when this endpoint only receives, a single <see cref="RtcpReceiverReport"/> (RFC 3550 §6.4.2) carries
/// them. Either way a single SDES packet with the session CNAME follows (RFC 3550 §6.5 — every compound
/// RTCP packet must carry a CNAME). When there is nothing to say (no sending track and no inbound source),
/// nothing is emitted.
/// </para>
/// <para>
/// This reporter sends valid report blocks and the LSR/DLSR needed for the peer to compute round-trip time,
/// and — via <c>onSenderReportSent</c> — publishes each SR's LSR and send instant so the session's outbound
/// quality tracker can match the peer's echoed report and derive our own RTT (RFC 3550 §6.4.1). The send path
/// fails closed until the DTLS-SRTP handshake installs the outbound SRTCP key, so starting the reporter before
/// keying is safe — the early ticks are simply suppressed.
/// </para>
/// <para>
/// Patterned on the TURN allocation refresh loop: the clock and delay are injected so the loop is
/// deterministically testable, <see cref="Start"/> is idempotent and thread-safe, and
/// <see cref="DisposeAsync"/> cancels and awaits the loop. A tick that throws is logged and the loop
/// continues — one failed report must not stop reporting for the session. The interval is not fixed: it is
/// recomputed after each report by <see cref="RtcpTransmissionInterval"/> (RFC 3550 §6.2/§6.3.1 — randomised,
/// member-scaled). On disposal, once at least one report has gone out, a best-effort RTCP BYE announces our
/// departure (RFC 3550 §6.6) over the same fail-closed SRTCP path, before the transport it rides is torn down.
/// </para>
/// </summary>
internal sealed class BundledRtcpReporter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    // RFC 3550 §6.4.1: an SR or RR carries at most 31 reception report blocks (the 5-bit RC field).
    private const int MaxReportBlocksPerReport = 31;

    // RFC 3550 §6.2: RTCP bandwidth (bits/s) governing the transmission interval — conventionally ~5% of the
    // session bandwidth. This default keeps the Tmin floor dominant for a 1:1 bundle while still scaling the
    // interval up for larger groups; the exact value only becomes visible once membership grows.
    private const double DefaultRtcpBandwidthBitsPerSecond = 5000.0;
    // §6.3.3: seeds the running average RTCP compound size until the first report measures a real one.
    private const double InitialAverageRtcpSizeBytes = 128.0;

    private readonly Func<IReadOnlyList<BundledSenderReportInfo>> _snapshotSenderReports;
    private readonly Func<IReadOnlyList<BundledReceptionReportBlock>> _snapshotReceptionBlocks;
    private readonly uint _localSsrc;
    private readonly Action<uint, uint, DateTimeOffset>? _onSenderReportSent;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendRtcp;
    private readonly IRtcpPacketCodec _codec;
    private readonly string _cname;
    private readonly TimeSpan _interval;
    private readonly RtcpTransmissionInterval _intervalCalculator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<BundledRtcpReporter> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Task? _loop;
    private bool _disposed;

    // Loop-thread-only interval state (RFC 3550 §6.3): the running average RTCP compound size, the next
    // scheduled interval, and whether any report has gone out (gates the teardown BYE, §6.6).
    private double _averageRtcpSize = InitialAverageRtcpSizeBytes;
    private TimeSpan _nextInterval;
    private bool _hasReported;

    /// <summary>
    /// Creates the reporter over a BUNDLE session's outbound send path.
    /// </summary>
    /// <param name="snapshotSenderReports">Snapshots the sending tracks' SR counters (RFC 3550 §6.4.1).</param>
    /// <param name="snapshotReceptionBlocks">
    /// Snapshots the per-SSRC inbound reception report blocks (RFC 3550 §6.4.1). Stateful — each call advances
    /// the fraction-lost interval baseline — so the reporter calls it exactly once per emitted report.
    /// </param>
    /// <param name="localSsrc">
    /// The SSRC that owns a Receiver Report (RFC 3550 §6.4.2) when this endpoint is receive-only. A Sender
    /// Report keys itself by each sending SSRC instead, so this only labels the RR.
    /// </param>
    /// <param name="sendRtcp">Protects and sends a plaintext RTCP compound packet (fail-closed SRTCP send path).</param>
    /// <param name="codec">Encodes the compound RTCP packet to the wire (RFC 3550 §6).</param>
    /// <param name="cname">The session canonical name emitted in the SDES CNAME item (RFC 3550 §6.5.1).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="interval">The reporting interval; defaults to 5 seconds.</param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    /// <param name="utcNow">The wall clock read for the SR NTP timestamp; injectable for deterministic tests.</param>
    /// <param name="onSenderReportSent">
    /// Optional callback invoked for each emitted Sender Report with (sending SSRC, the SR's LSR — the middle 32
    /// bits of its NTP timestamp — , the wall-clock send instant). Feeds the outbound quality tracker's RTT
    /// computation (RFC 3550 §6.4.1); null when RTT is not tracked.
    /// </param>
    public BundledRtcpReporter(
        Func<IReadOnlyList<BundledSenderReportInfo>> snapshotSenderReports,
        Func<IReadOnlyList<BundledReceptionReportBlock>> snapshotReceptionBlocks,
        uint localSsrc,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendRtcp,
        IRtcpPacketCodec codec,
        string cname,
        ILoggerFactory loggerFactory,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<uint, uint, DateTimeOffset>? onSenderReportSent = null)
    {
        _snapshotSenderReports = snapshotSenderReports ?? throw new ArgumentNullException(nameof(snapshotSenderReports));
        _snapshotReceptionBlocks = snapshotReceptionBlocks ?? throw new ArgumentNullException(nameof(snapshotReceptionBlocks));
        _localSsrc = localSsrc;
        _onSenderReportSent = onSenderReportSent;
        _sendRtcp = sendRtcp ?? throw new ArgumentNullException(nameof(sendRtcp));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _cname = cname ?? throw new ArgumentNullException(nameof(cname));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _interval = interval is { } explicitInterval && explicitInterval > TimeSpan.Zero
            ? explicitInterval
            : DefaultInterval;
        _intervalCalculator = new RtcpTransmissionInterval(_interval, DefaultRtcpBandwidthBitsPerSecond);
        _delay = delay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _logger = loggerFactory.CreateLogger<BundledRtcpReporter>();
    }

    /// <summary>
    /// Starts the reporting loop on a background task. Idempotent and thread-safe; a second call, or a call
    /// after disposal, is a no-op.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
                return;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // RFC 3550 §6.2: the first report waits half Tmin; every later interval is recomputed after its report.
        _nextInterval = _intervalCalculator.Compute(members: 1, senders: 0, weSent: false, _averageRtcpSize, initial: true);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(_nextInterval, ct).ConfigureAwait(false);
                await SendReportAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Bundled RTCP reporter stopped.");
                return;
            }
            catch (Exception ex)
            {
                // One failed report (encode/send hiccup) must not stop reporting for the whole session:
                // log and keep looping so the next interval tries again.
                _logger.LogWarning(ex, "Bundled RTCP report failed; will retry on the next interval.");
            }
        }
    }

    private async ValueTask SendReportAsync(CancellationToken ct)
    {
        // Capture reception blocks exactly once per report: the snapshot advances each source's fraction-lost
        // interval baseline (RFC 3550 §A.3), so it must run whether or not this endpoint is also sending.
        var receptionBlocks = ToReportBlocks(_snapshotReceptionBlocks());
        var senders = _snapshotSenderReports();

        // RFC 3550 §6.2 membership estimate for the interval calculation: self plus each distinct inbound
        // source, with our sending tracks and every inbound source counting as senders.
        var members = 1 + receptionBlocks.Count;
        var senderCount = senders.Count + receptionBlocks.Count;
        var weSent = senders.Count > 0;

        // Nothing to report: no track has sent and no inbound source is being received. Still reschedule so the
        // loop keeps ticking and starts reporting as soon as media flows.
        if (senders.Count == 0 && receptionBlocks.Count == 0)
        {
            _nextInterval = _intervalCalculator.Compute(members, senderCount, weSent, _averageRtcpSize, initial: false);
            return;
        }

        var now = _utcNow();
        var ntp = ToNtpTimestamp(now);
        var packets = new List<RtcpPacket>(Math.Max(senders.Count, 1) + 1);
        var sdesChunks = new List<RtcpSdesChunk>();

        if (senders.Count > 0)
        {
            BuildSenderReports(senders, receptionBlocks, ntp, now, packets, sdesChunks);
        }
        else
        {
            // Receive-only: a single Receiver Report (RFC 3550 §6.4.2) keyed by the local SSRC carries the blocks.
            packets.Add(new RtcpReceiverReport { Ssrc = _localSsrc, ReportBlocks = receptionBlocks });
            sdesChunks.Add(SdesChunk(_localSsrc));
        }

        packets.Add(new RtcpSdesPacket { Chunks = sdesChunks });

        var datagram = _codec.Encode(packets);
        await _sendRtcp(datagram, ct).ConfigureAwait(false);

        // RFC 3550 §6.3.3: fold the sent size into the running average, record that we have reported (gates the
        // teardown BYE), and schedule the next interval from the observed membership.
        _averageRtcpSize += (datagram.Length - _averageRtcpSize) / 16.0;
        _hasReported = true;
        _nextInterval = _intervalCalculator.Compute(members, senderCount, weSent, _averageRtcpSize, initial: false);
    }

    // One Sender Report per sending SSRC (RFC 3550 §6.4.1). The reception blocks are attached to the SRs,
    // packed 31-per-report (the RC field limit) so a session receiving more than 31 sources still emits every
    // block; the first SR takes the first 31, the next SR the following 31, and so on. Any block left over
    // after the SRs are full (more inbound sources than 31×senders) is dropped this interval and reported the
    // next — an edge case far outside a normal BUNDLE session's stream count.
    private void BuildSenderReports(
        IReadOnlyList<BundledSenderReportInfo> senders,
        IReadOnlyList<RtcpReportBlock> receptionBlocks,
        ulong ntp,
        DateTimeOffset now,
        List<RtcpPacket> packets,
        List<RtcpSdesChunk> sdesChunks)
    {
        var srMiddle32 = ToMiddle32Bits(ntp);
        var blockOffset = 0;
        foreach (var sender in senders)
        {
            var take = Math.Min(MaxReportBlocksPerReport, receptionBlocks.Count - blockOffset);
            IReadOnlyList<RtcpReportBlock> blocks = take > 0
                ? receptionBlocks.Skip(blockOffset).Take(take).ToArray()
                : [];
            blockOffset += take;

            packets.Add(new RtcpSenderReport
            {
                Ssrc = sender.Ssrc,
                NtpTimestamp = ntp,
                RtpTimestamp = sender.LastRtpTimestamp,
                // The SR counters are 32-bit on the wire (RFC 3550 §6.4.1); the tracked longs are cumulative
                // and, in practice, well within range — wrap deliberately as the RFC's counters do.
                SenderPacketCount = unchecked((uint)sender.PacketCount),
                SenderOctetCount = unchecked((uint)sender.OctetCount),
                ReportBlocks = blocks,
            });
            sdesChunks.Add(SdesChunk(sender.Ssrc));

            // Publish this SR's LSR + send instant so the quality tracker can match a peer's echoed report and
            // derive RTT (RFC 3550 §6.4.1). All SRs in one compound share the same NTP timestamp/send instant.
            _onSenderReportSent?.Invoke(sender.Ssrc, srMiddle32, now);
        }
    }

    private RtcpSdesChunk SdesChunk(uint ssrc) => new()
    {
        Ssrc = ssrc,
        Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = _cname }],
    };

    private static IReadOnlyList<RtcpReportBlock> ToReportBlocks(IReadOnlyList<BundledReceptionReportBlock> blocks)
    {
        if (blocks.Count == 0)
            return [];

        var result = new RtcpReportBlock[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            result[i] = new RtcpReportBlock
            {
                Ssrc = b.Ssrc,
                FractionLost = b.FractionLost,
                CumulativePacketsLost = b.CumulativePacketsLost,
                ExtendedHighestSeq = b.ExtendedHighestSequenceNumber,
                Jitter = b.InterarrivalJitter,
                LastSr = b.LastSr,
                DelaySinceLastSr = b.DelaySinceLastSr,
            };
        }

        return result;
    }

    /// <summary>
    /// Cancels the reporting loop, awaits it, then sends a best-effort teardown BYE (RFC 3550 §6.6, only if we
    /// reported before). Idempotent. Must run before the transport the reporter's send rides is disposed — a
    /// composition-layer ordering concern — so the BYE is still keyed and transported.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.ConfigureAwait(false);
        await SendGoodbyeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    // RFC 3550 §6.6: announce our departure with a BYE for the SSRC(s) we were using. Sent after the loop has
    // stopped (no concurrent report) but while the transport/SRTCP is still up — the session disposes the
    // reporter before DTLS/transport. Only when we actually reported before (a member that never announced
    // itself must not BYE). Best-effort and fail-closed like every report; failure is logged, never thrown.
    private async ValueTask SendGoodbyeAsync()
    {
        if (!_hasReported)
            return;

        try
        {
            var senders = _snapshotSenderReports();
            IReadOnlyList<uint> sources = senders.Count > 0
                ? senders.Select(s => s.Ssrc).ToArray()
                : [_localSsrc];

            // §6.1: a compound must begin with an SR/RR and carry a CNAME; at teardown we lead with an empty RR
            // for our SSRC, the SDES CNAME, then the BYE.
            var packets = new List<RtcpPacket>
            {
                new RtcpReceiverReport { Ssrc = _localSsrc, ReportBlocks = [] },
                new RtcpSdesPacket { Chunks = [SdesChunk(_localSsrc)] },
                new RtcpByePacket { Sources = sources, Reason = "leaving" },
            };

            var datagram = _codec.Encode(packets);
            await _sendRtcp(datagram, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bundled RTCP BYE on teardown could not be sent (best-effort).");
        }
    }

    // RFC 3550 §6.4.1 NTP timestamp: seconds since 1 January 1900 in the upper 32 bits, the 2^-32 fraction
    // in the lower 32. Mirrors the SIP-path monitor so both report the same wall-clock format.
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

    // RFC 3550 §6.4.1: the LSR a peer echoes is the middle 32 bits of the sender's 64-bit NTP timestamp.
    private static uint ToMiddle32Bits(ulong ntpTimestamp) => (uint)((ntpTimestamp >> 16) & 0xFFFFFFFF);
}
