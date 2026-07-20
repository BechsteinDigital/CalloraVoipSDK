using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One outbound media stream on a bundled transport (ADR-011 B2c-in-4, RFC 8843): it owns the stream's
/// SSRC, RTP sequence counter, timestamp cursor, default payload type, and the MID stamper (ADR-010
/// B2c-out) that marks every packet with this m-line's MID (RFC 9143) so the peer can associate the
/// SSRC with the m-line. It builds RTP packets only — encryption with the transport's shared SRTP
/// context and the send over the shared socket belong to <see cref="BundledOutboundPipeline"/>.
/// </summary>
/// <remarks>
/// Thread-safe on its own: the sequence and timestamp cursors are advanced under a per-track lock, so
/// concurrent sends on the same track stay sequence-consistent. Each track advances its own state
/// independently — the shared SRTP context keys ROC/replay per SSRC (ADR-011 B2c-in-1).
/// </remarks>
internal sealed class BundledOutboundTrack
{
    private readonly uint _ssrc;
    private readonly byte _defaultPayloadType;
    private readonly int _samplesPerPacket;
    private readonly uint _clockRate;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly RtpOutboundHeaderExtensionStamper _stamper;
    private readonly object _sendSync = new();

    private ushort _sequenceNumber;
    private uint _timestamp;

    // Cumulative RTCP Sender Report counters for this SSRC (RFC 3550 §6.4.1), advanced under _sendSync
    // after each packet the pipeline actually sent: total packets, total payload octets (excluding RTP
    // headers), the RTP timestamp of the last sent packet, and the wall-clock instant that timestamp was
    // assigned (CF-004e — lets the reporter extrapolate the SR's RTP timestamp onto the report instant across a
    // send pause/DTX). The reporter reads them via a snapshot.
    private long _senderPacketCount;
    private long _senderOctetCount;
    private uint _lastRtpTimestamp;
    private DateTimeOffset _lastRtpTimestampAtUtc;
    private bool _hasSent;

    /// <summary>
    /// Creates a track. <paramref name="initialSequenceNumber"/> and <paramref name="initialTimestamp"/>
    /// seed the RTP cursors (RFC 3550 §5.1 recommends random starting values — the caller supplies them
    /// so the track stays deterministic and testable).
    /// </summary>
    /// <param name="ssrc">The stream's synchronisation source.</param>
    /// <param name="defaultPayloadType">The default RTP payload type used when a send does not override it.</param>
    /// <param name="samplesPerPacket">The RTP timestamp increment per cursor-advancing packet.</param>
    /// <param name="stamper">The header-extension stamper (MID, RID, transport-cc).</param>
    /// <param name="initialSequenceNumber">The initial RTP sequence number (RFC 3550 §5.1).</param>
    /// <param name="initialTimestamp">The initial RTP timestamp cursor (RFC 3550 §5.1).</param>
    /// <param name="clockRate">
    /// The track's RTP clock rate (Hz), used by the reporter to extrapolate the SR's RTP timestamp onto the
    /// report instant (CF-004e). Zero disables that extrapolation (the last sent timestamp is used as-is).
    /// </param>
    /// <param name="utcNow">The wall clock read to timestamp each send; injectable for deterministic tests.</param>
    public BundledOutboundTrack(
        uint ssrc,
        byte defaultPayloadType,
        int samplesPerPacket,
        RtpOutboundHeaderExtensionStamper stamper,
        ushort initialSequenceNumber,
        uint initialTimestamp,
        uint clockRate = 0,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(stamper);
        ArgumentOutOfRangeException.ThrowIfNegative(samplesPerPacket);

        _ssrc               = ssrc;
        _defaultPayloadType = defaultPayloadType;
        _samplesPerPacket   = samplesPerPacket;
        _clockRate          = clockRate;
        _utcNow             = utcNow ?? (() => DateTimeOffset.UtcNow);
        _stamper            = stamper;
        _sequenceNumber     = initialSequenceNumber;
        _timestamp          = initialTimestamp;
    }

    /// <summary>The stream's synchronisation source.</summary>
    public uint Ssrc => _ssrc;

    /// <summary>The default RTP payload type used when a send does not override it.</summary>
    public byte DefaultPayloadType => _defaultPayloadType;

    /// <summary>Total RTP packets this track has actually sent (RFC 3550 §6.4.1 sender's packet count).</summary>
    public long SenderPacketCount { get { lock (_sendSync) return _senderPacketCount; } }

    /// <summary>Total RTP payload octets sent, excluding headers (RFC 3550 §6.4.1 sender's octet count).</summary>
    public long SenderOctetCount { get { lock (_sendSync) return _senderOctetCount; } }

    /// <summary>Whether this track has sent at least one packet (so a Sender Report is warranted).</summary>
    public bool HasSent { get { lock (_sendSync) return _hasSent; } }

    /// <summary>The RTP timestamp of the last packet this track sent (0 before the first send).</summary>
    public uint LastRtpTimestamp { get { lock (_sendSync) return _lastRtpTimestamp; } }

    /// <summary>
    /// The track's current outbound timestamp cursor (the timestamp the next cursor-advancing media packet
    /// would carry). Read under the track lock so it is consistent with a concurrent send. Used to stamp an
    /// out-of-band RFC 4733 telephone-event so the event shares the audio stream's timestamp clock.
    /// </summary>
    public uint CurrentTimestamp { get { lock (_sendSync) return _timestamp; } }

    /// <summary>
    /// Atomically snapshots this track's Sender Report counters (RFC 3550 §6.4.1) under a single lock, so the
    /// packet count, octet count, and last RTP timestamp are consistent with one another (reading them through
    /// the individual properties would take the lock four separate times and could tear a report across a
    /// concurrent send). Returns <see langword="null"/> when the track has not sent yet — no SR is due for it.
    /// </summary>
    public BundledSenderReportInfo? Snapshot()
    {
        lock (_sendSync)
        {
            if (!_hasSent)
                return null;
            return new BundledSenderReportInfo(
                _ssrc, _senderPacketCount, _senderOctetCount, _lastRtpTimestamp, _lastRtpTimestampAtUtc, _clockRate);
        }
    }

    /// <summary>
    /// Records that one packet with <paramref name="payloadOctetCount"/> payload octets and RTP timestamp
    /// <paramref name="rtpTimestamp"/> was actually sent, advancing this track's Sender Report counters
    /// (RFC 3550 §6.4.1). The pipeline calls it after a successful send. Thread-safe under the track lock.
    /// </summary>
    public void RecordSent(int payloadOctetCount, uint rtpTimestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadOctetCount);
        lock (_sendSync)
        {
            _senderPacketCount++;
            _senderOctetCount += payloadOctetCount;
            _lastRtpTimestamp = rtpTimestamp;
            // Capture the wall clock alongside the RTP timestamp so the reporter can extrapolate the SR's RTP
            // timestamp onto the report instant (CF-004e). Read under the send lock so the pair stays consistent.
            _lastRtpTimestampAtUtc = _utcNow();
            _hasSent = true;
        }
    }

    /// <summary>
    /// Builds the next RTP packet for this track: assigns the sequence number and timestamp, stamps the
    /// MID header extension, and returns the packet ready for the pipeline to encode, encrypt, and send.
    /// </summary>
    /// <param name="payload">The media payload.</param>
    /// <param name="marker">The RTP marker bit.</param>
    /// <param name="payloadType">The payload type for this packet.</param>
    /// <param name="timestampOverride">
    /// An explicit RTP timestamp (video frames share one frame-level timestamp across packets), or
    /// <see langword="null"/> to use the running cursor.
    /// </param>
    /// <param name="advanceTimestamp">
    /// Whether to advance the timestamp cursor by one packet's samples afterwards. Media frames advance;
    /// packets carrying an explicit timestamp (a shared video frame timestamp) do not.
    /// </param>
    public RtpPacket BuildPacket(
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint? timestampOverride,
        bool advanceTimestamp)
    {
        lock (_sendSync)
        {
            var sequenceNumber = _sequenceNumber;
            var timestamp = timestampOverride ?? _timestamp;

            unchecked { _sequenceNumber++; } // wraps at 65535 per RFC 3550 §5.1
            if (advanceTimestamp)
                _timestamp += (uint)_samplesPerPacket;

            return new RtpPacket
            {
                PayloadType     = payloadType,
                Marker          = marker,
                SequenceNumber  = sequenceNumber,
                Timestamp       = timestamp,
                Ssrc            = _ssrc,
                Payload         = payload,
                // Stamp MID (and, once wired, transport-cc) before SRTP: RFC 3711 authenticates but does
                // not encrypt the header extension, so the peer reads the MID in the clear to demux.
                HeaderExtension = _stamper.Build(transportCcSequence: null),
            };
        }
    }
}
