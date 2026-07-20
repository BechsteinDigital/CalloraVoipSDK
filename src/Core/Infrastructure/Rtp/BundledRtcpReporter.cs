using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Periodically emits RTCP Sender Reports for a BUNDLE media session's active outbound streams
/// (RFC 3550 §6.4). A background loop, every <c>interval</c>, snapshots each sending track's SR counters
/// (<see cref="BundledOutboundPipeline.SnapshotSenderReports"/>) and builds one compound RTCP packet — a
/// <see cref="RtcpSenderReport"/> per SSRC that has sent, followed by a single SDES packet carrying the
/// session CNAME for every reporting SSRC (RFC 3550 §6.5, every compound RTCP packet must carry a CNAME) —
/// then protects and sends it via the outbound pipeline's SRTCP send path. When no track has sent yet,
/// nothing is emitted.
/// <para>
/// This slice sends Sender Reports and SDES only. Receiver Reports (reception statistics for inbound
/// streams) and the derived round-trip time are a later slice, so the Sender Reports carry no report
/// blocks. The send path fails closed until the DTLS-SRTP handshake installs the outbound SRTCP key, so
/// starting the reporter before keying is safe — the early ticks are simply suppressed.
/// </para>
/// <para>
/// Patterned on the TURN allocation refresh loop: the clock and delay are injected so the loop is
/// deterministically testable, <see cref="Start"/> is idempotent and thread-safe, and
/// <see cref="DisposeAsync"/> cancels and awaits the loop. A tick that throws is logged and the loop
/// continues — one failed report must not stop reporting for the session. There is no teardown packet
/// (unlike an allocation refresh); the session's DTLS BYE/close handles shutdown.
/// </para>
/// </summary>
internal sealed class BundledRtcpReporter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlyList<BundledSenderReportInfo>> _snapshotSenderReports;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendRtcp;
    private readonly IRtcpPacketCodec _codec;
    private readonly string _cname;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<BundledRtcpReporter> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Creates the reporter over a BUNDLE session's outbound send path.
    /// </summary>
    /// <param name="snapshotSenderReports">Snapshots the sending tracks' SR counters (RFC 3550 §6.4.1).</param>
    /// <param name="sendRtcp">Protects and sends a plaintext RTCP compound packet (fail-closed SRTCP send path).</param>
    /// <param name="codec">Encodes the compound RTCP packet to the wire (RFC 3550 §6).</param>
    /// <param name="cname">The session canonical name emitted in the SDES CNAME item (RFC 3550 §6.5.1).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="interval">The reporting interval; defaults to 5 seconds.</param>
    /// <param name="delay">The delay primitive; injectable for deterministic tests.</param>
    /// <param name="utcNow">The wall clock read for the SR NTP timestamp; injectable for deterministic tests.</param>
    public BundledRtcpReporter(
        Func<IReadOnlyList<BundledSenderReportInfo>> snapshotSenderReports,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendRtcp,
        IRtcpPacketCodec codec,
        string cname,
        ILoggerFactory loggerFactory,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _snapshotSenderReports = snapshotSenderReports ?? throw new ArgumentNullException(nameof(snapshotSenderReports));
        _sendRtcp = sendRtcp ?? throw new ArgumentNullException(nameof(sendRtcp));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _cname = cname ?? throw new ArgumentNullException(nameof(cname));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _interval = interval is { } explicitInterval && explicitInterval > TimeSpan.Zero
            ? explicitInterval
            : DefaultInterval;
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(_interval, ct).ConfigureAwait(false);
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
        var senders = _snapshotSenderReports();
        if (senders.Count == 0)
            return; // no track has sent yet — nothing to report

        var ntp = ToNtpTimestamp(_utcNow());
        var packets = new List<RtcpPacket>(senders.Count + 1);
        var chunks = new List<RtcpSdesChunk>(senders.Count);

        foreach (var sender in senders)
        {
            packets.Add(new RtcpSenderReport
            {
                Ssrc = sender.Ssrc,
                NtpTimestamp = ntp,
                RtpTimestamp = sender.LastRtpTimestamp,
                // The SR counters are 32-bit on the wire (RFC 3550 §6.4.1); the tracked longs are cumulative
                // and, in practice, well within range — wrap deliberately as the RFC's counters do.
                SenderPacketCount = unchecked((uint)sender.PacketCount),
                SenderOctetCount = unchecked((uint)sender.OctetCount),
                ReportBlocks = [], // reception report blocks are a later slice (RR/RTT)
            });
            chunks.Add(new RtcpSdesChunk
            {
                Ssrc = sender.Ssrc,
                Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = _cname }],
            });
        }

        packets.Add(new RtcpSdesPacket { Chunks = chunks });

        var datagram = _codec.Encode(packets);
        await _sendRtcp(datagram, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the reporting loop and awaits it. Idempotent. Sends no teardown packet (the session's DTLS
    /// close handles shutdown); must run before the transport the reporter's send rides is disposed — a
    /// composition-layer ordering concern.
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
        _cts.Dispose();
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
}
