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
    /// Default hard cap on a reassembled encoded video frame (and H.264 FU-A fragment) — a remote peer
    /// controls how much a depacketiser accumulates per track/RID lane, so it must be bounded even after
    /// SRTP authentication (K4). 1 MiB matches SIPSorcery's reassembly cap and comfortably exceeds any real
    /// coded frame.
    /// </summary>
    internal const int DefaultMaxEncodedFrameBytes = 1024 * 1024;

    /// <summary>
    /// Creates the packetiser/depacketiser pair for the codec, matched case-insensitively by name.
    /// </summary>
    /// <param name="codecName">Negotiated codec name (e.g. <c>VP8</c>, <c>H264</c>).</param>
    /// <param name="maxEncodedFrameBytes">Hard reassembly cap for the depacketiser (K4); see the default.</param>
    /// <exception cref="InvalidOperationException">The codec has no RTP payload-format implementation.</exception>
    public static (IVideoPacketiser Packetiser, IVideoDepacketiser Depacketiser) Create(
        string codecName, int maxEncodedFrameBytes = DefaultMaxEncodedFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(codecName);
        return codecName.ToUpperInvariant() switch
        {
            "VP8" => (new Vp8Packetiser(), new Vp8Depacketiser(maxEncodedFrameBytes)),
            "H264" => (new H264Packetiser(), new H264Depacketiser(maxEncodedFrameBytes)),
            _ => throw new InvalidOperationException(
                $"Negotiated video codec '{codecName}' has no RTP payload format implementation."),
        };
    }
}
