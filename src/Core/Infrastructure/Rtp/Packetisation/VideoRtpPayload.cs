namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// One RTP payload produced by packetising an encoded video frame. All payloads of one
/// frame share the RTP timestamp; <see cref="IsLastOfFrame"/> maps to the RTP marker bit
/// (RFC 6184 §5.1 / RFC 7741 §4.1: marker set on the last packet of an access unit/frame).
/// A struct — video frames fan out into dozens of payloads on the send hotpath.
/// </summary>
internal readonly record struct VideoRtpPayload
{
    /// <summary>The RTP payload bytes (codec payload header + fragment data).</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>True on the final payload of the frame — sets the RTP marker bit.</summary>
    public required bool IsLastOfFrame { get; init; }
}
