namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// One encoded media frame received on a <see cref="RemoteTrack"/>. Transport-only: <see cref="Payload"/>
/// is the raw depacketised codec bitstream — the app owns decoding.
/// </summary>
public readonly struct EncodedFrame
{
    /// <summary>Creates an encoded frame.</summary>
    public EncodedFrame(ReadOnlyMemory<byte> payload, uint? rtpTimestamp, bool isKeyFrame, long? presentationTimeUsec, string? rid = null)
    {
        Payload = payload;
        RtpTimestamp = rtpTimestamp;
        IsKeyFrame = isKeyFrame;
        PresentationTimeUsec = presentationTimeUsec;
        Rid = rid;
    }

    /// <summary>The encoded codec payload. Valid for the duration of the <see cref="RemoteTrack.FrameReceived"/> callback.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// The frame's RTP timestamp (RFC 3550 §5.1) when known — surfaced for both audio and video inbound frames,
    /// so an SFU can forward the stream with a monotonic clock. <see langword="null"/> only when the producer
    /// did not stamp one.
    /// </summary>
    public uint? RtpTimestamp { get; }

    /// <summary>Whether this is a key/intra frame. Always <see langword="false"/> for audio.</summary>
    public bool IsKeyFrame { get; }

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
}
