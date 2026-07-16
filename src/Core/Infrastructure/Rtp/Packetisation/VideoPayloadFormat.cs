namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// Maps a negotiated video codec name to its RTP payload-format pair — the packetiser that fragments
/// encoded frames into RTP payloads and the depacketiser that reassembles them (H.264 RFC 6184,
/// VP8 RFC 7741). Shared so both the single-stream <see cref="Session.RtpSession"/>-backed video path
/// and the bundled transport's <see cref="BundledVideoTrack"/> resolve the same pair the same way.
/// </summary>
internal static class VideoPayloadFormat
{
    /// <summary>
    /// Creates the packetiser/depacketiser pair for the codec, matched case-insensitively by name.
    /// </summary>
    /// <exception cref="InvalidOperationException">The codec has no RTP payload-format implementation.</exception>
    public static (IVideoPacketiser Packetiser, IVideoDepacketiser Depacketiser) Create(string codecName)
    {
        ArgumentNullException.ThrowIfNull(codecName);
        return codecName.ToUpperInvariant() switch
        {
            "VP8" => (new Vp8Packetiser(), new Vp8Depacketiser()),
            "H264" => (new H264Packetiser(), new H264Depacketiser()),
            _ => throw new InvalidOperationException(
                $"Negotiated video codec '{codecName}' has no RTP payload format implementation."),
        };
    }
}
