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
    private readonly RtpOutboundHeaderExtensionStamper _stamper;
    private readonly object _sendSync = new();

    private ushort _sequenceNumber;
    private uint _timestamp;

    /// <summary>
    /// Creates a track. <paramref name="initialSequenceNumber"/> and <paramref name="initialTimestamp"/>
    /// seed the RTP cursors (RFC 3550 §5.1 recommends random starting values — the caller supplies them
    /// so the track stays deterministic and testable).
    /// </summary>
    public BundledOutboundTrack(
        uint ssrc,
        byte defaultPayloadType,
        int samplesPerPacket,
        RtpOutboundHeaderExtensionStamper stamper,
        ushort initialSequenceNumber,
        uint initialTimestamp)
    {
        ArgumentNullException.ThrowIfNull(stamper);
        ArgumentOutOfRangeException.ThrowIfNegative(samplesPerPacket);

        _ssrc               = ssrc;
        _defaultPayloadType = defaultPayloadType;
        _samplesPerPacket   = samplesPerPacket;
        _stamper            = stamper;
        _sequenceNumber     = initialSequenceNumber;
        _timestamp          = initialTimestamp;
    }

    /// <summary>The stream's synchronisation source.</summary>
    public uint Ssrc => _ssrc;

    /// <summary>The default RTP payload type used when a send does not override it.</summary>
    public byte DefaultPayloadType => _defaultPayloadType;

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
