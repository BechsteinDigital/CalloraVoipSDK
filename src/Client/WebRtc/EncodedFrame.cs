namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// One encoded media frame received on a <see cref="RemoteTrack"/>. Transport-only: <see cref="Payload"/>
/// is the raw depacketised codec bitstream — the app owns decoding.
/// </summary>
public readonly struct EncodedFrame
{
    /// <summary>Creates an encoded frame whose sender declared no layer information.</summary>
    public EncodedFrame(ReadOnlyMemory<byte> payload, uint? rtpTimestamp, bool isKeyFrame, long? presentationTimeUsec, string? rid = null)
        : this(payload, rtpTimestamp, isKeyFrame, presentationTimeUsec, rid, spatialId: null, temporalId: null)
    {
    }

    /// <summary>
    /// Creates an encoded frame carrying the layer the sender declared in its Dependency Descriptor (#225).
    /// </summary>
    /// <param name="payload">The encoded codec payload.</param>
    /// <param name="rtpTimestamp">The frame's RTP timestamp, or <see langword="null"/> when unstamped.</param>
    /// <param name="isKeyFrame">Whether the frame can be decoded on its own.</param>
    /// <param name="presentationTimeUsec">The wall-clock presentation time in microseconds, when known.</param>
    /// <param name="rid">The frame's simulcast <c>a=rid</c>, or <see langword="null"/>.</param>
    /// <param name="spatialId">The frame's spatial layer, or <see langword="null"/> when unknown.</param>
    /// <param name="temporalId">The frame's temporal layer, or <see langword="null"/> when unknown.</param>
    /// <param name="keyFrameSource">Where <paramref name="isKeyFrame"/> came from.</param>
    public EncodedFrame(
        ReadOnlyMemory<byte> payload,
        uint? rtpTimestamp,
        bool isKeyFrame,
        long? presentationTimeUsec,
        string? rid,
        int? spatialId,
        int? temporalId,
        KeyFrameSource keyFrameSource = KeyFrameSource.Unknown)
    {
        Payload = payload;
        RtpTimestamp = rtpTimestamp;
        IsKeyFrame = isKeyFrame;
        PresentationTimeUsec = presentationTimeUsec;
        Rid = rid;
        SpatialId = spatialId;
        TemporalId = temporalId;
        KeyFrameSource = keyFrameSource;
    }

    /// <summary>The encoded codec payload. Valid for the duration of the <see cref="RemoteTrack.FrameReceived"/> callback.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// The frame's RTP timestamp (RFC 3550 §5.1) when known — surfaced for both audio and video inbound frames,
    /// so an SFU can forward the stream with a monotonic clock. <see langword="null"/> only when the producer
    /// did not stamp one.
    /// </summary>
    public uint? RtpTimestamp { get; }

    /// <summary>
    /// Whether this is a key/intra frame. Always <see langword="false"/> for audio. Read it together with
    /// <see cref="KeyFrameSource"/>: <see langword="false"/> from
    /// <see cref="WebRtc.KeyFrameSource.Unknown"/> means nobody answered, not that the answer was no.
    /// </summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// Where <see cref="IsKeyFrame"/> came from, so a forwarder can decide how far to trust it — the RTP
    /// header extension holds under end-to-end encryption, a payload-derived answer only holds as far as the
    /// payload is readable.
    /// </summary>
    public KeyFrameSource KeyFrameSource { get; }

    /// <summary>
    /// The wall-clock presentation time in microseconds for lip-sync across tracks, when known;
    /// <see langword="null"/> until the RTCP-SR RTP↔NTP mapping lands (ADR-012 deferred item).
    /// </summary>
    public long? PresentationTimeUsec { get; }

    /// <summary>
    /// The frame's <c>a=rid</c> simulcast encoding id (RFC 8852) for receive-side simulcast-layer
    /// discrimination / SFU forwarding; <see langword="null"/> for the non-simulcast/primary (RID-less)
    /// stream and for audio.
    /// </summary>
    public string? Rid { get; }

    /// <summary>
    /// The frame's spatial layer from the sender's Dependency Descriptor (AV1 RTP specification §A), so a
    /// forwarder can pick a layer without decoding — or, for an end-to-end encrypted stream, without being
    /// able to. <see langword="null"/> when the peer did not negotiate the descriptor, the frame carried
    /// none, or the stream was joined before the sender declared its layer structure.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Rid"/>: simulcast sends each encoding as its own stream, whereas the
    /// descriptor describes layers <em>within</em> one stream (scalable/SVC). A peer may use either, both, or
    /// neither.
    /// </remarks>
    public int? SpatialId { get; }

    /// <summary>
    /// The frame's temporal layer from the sender's Dependency Descriptor, reported under the same conditions
    /// as <see cref="SpatialId"/>.
    /// </summary>
    public int? TemporalId { get; }
}
