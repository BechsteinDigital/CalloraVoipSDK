namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// One encoded media frame captured for recording, tagged with its track kind and direction. Transport-only:
/// <see cref="Payload"/> is the raw depacketised codec bitstream (the recorder does not decode).
/// </summary>
public readonly struct RecordedFrame
{
    /// <summary>Creates a recorded frame.</summary>
    public RecordedFrame(TrackKind kind, MediaDirection direction, ReadOnlyMemory<byte> payload, uint? rtpTimestamp, bool isKeyFrame, string? rid = null)
    {
        Kind = kind;
        Direction = direction;
        Payload = payload;
        RtpTimestamp = rtpTimestamp;
        IsKeyFrame = isKeyFrame;
        Rid = rid;
    }

    /// <summary>Whether this is an audio or video frame.</summary>
    public TrackKind Kind { get; }

    /// <summary>Whether the frame was received from or sent to the remote peer.</summary>
    public MediaDirection Direction { get; }

    /// <summary>The encoded codec payload. Valid only for the duration of the <see cref="IEncodedMediaSink.Write"/> call.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The frame's RTP timestamp when known (video); <see langword="null"/> for audio.</summary>
    public uint? RtpTimestamp { get; }

    /// <summary>Whether this is a key/intra frame (video). Always <see langword="false"/> for audio.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// The simulcast layer id (RFC 8853 <c>a=rid</c>) for an outbound simulcast video frame, so a sink can
    /// separate the layers; <see langword="null"/> for a single-stream send, for audio, and for inbound
    /// frames (receive-side RID demux is a later slice).
    /// </summary>
    public string? Rid { get; }
}
