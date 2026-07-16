namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One m-line's configuration on a bundled transport (RFC 8843): its MID, synchronisation source, and
/// payload type. A video track additionally names its codec (H.264/VP8) so the session builds a
/// <see cref="BundledVideoTrack"/> for it; an audio track leaves <see cref="VideoCodecName"/> null and
/// exchanges raw RTP packets.
/// </summary>
internal sealed record BundledTrackConfig
{
    /// <summary>The m-line's MID token (<c>a=mid</c>), e.g. "audio" or "video".</summary>
    public required string Mid { get; init; }

    /// <summary>The stream's outbound synchronisation source.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>The negotiated RTP payload type for this m-line.</summary>
    public required byte PayloadType { get; init; }

    /// <summary>
    /// RTP timestamp increment per outbound audio packet (frame samples, e.g. 160 for 20 ms PCMU,
    /// 960 for 20 ms Opus). Ignored for a video track, whose packets carry an explicit frame timestamp.
    /// </summary>
    public int SamplesPerPacket { get; init; }

    /// <summary>The video codec name ("H264"/"VP8") for a video m-line, or null for an audio m-line.</summary>
    public string? VideoCodecName { get; init; }
}
