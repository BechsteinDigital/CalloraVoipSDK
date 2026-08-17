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

    /// <summary>
    /// Creates the payload-format pair for a codec whose frame content the SDK must not read — end-to-end
    /// encrypted media (WebRTC Encoded Transform / SFrame, RFC 9605), where the frame is ciphertext (#223).
    /// Both halves work from the RTP framing headers alone and never interpret the frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VP8 keeps <see cref="Vp8Packetiser"/> on the send side: it already prepends only the payload descriptor
    /// and never looks at the frame, so it is opaque as it stands. Only the receive side changes — the
    /// non-opaque depacketiser reads the key-frame bit out of the frame's first byte, which is ciphertext.
    /// </para>
    /// <para>
    /// H.264 needs both halves replaced, because the non-opaque pair parses Annex-B on send and dispatches on
    /// NAL types on receive. The opaque pair synthesises the NAL header RFC 6184 demands and carries the frame
    /// verbatim; see <see cref="OpaqueH264Packetiser"/> for the interop scope that follows from that, and
    /// ADR-068 for the decision.
    /// </para>
    /// <para>
    /// In both cases the reassembled frame carries no key-frame claim: that signal has to arrive in a plaintext
    /// RTP header extension (Dependency Descriptor), which is tracked as follow-up work in #223.
    /// </para>
    /// </remarks>
    /// <param name="codecName">Negotiated codec name (e.g. <c>VP8</c>, <c>H264</c>).</param>
    /// <param name="maxEncodedFrameBytes">Hard reassembly cap for the depacketiser (K4); see the default.</param>
    /// <exception cref="InvalidOperationException">The codec has no opaque RTP payload-format implementation.</exception>
    public static (IVideoPacketiser Packetiser, IVideoDepacketiser Depacketiser) CreateOpaque(
        string codecName, int maxEncodedFrameBytes = DefaultMaxEncodedFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(codecName);
        return codecName.ToUpperInvariant() switch
        {
            "VP8" => (new Vp8Packetiser(), new OpaqueVp8Depacketiser(maxEncodedFrameBytes)),
            "H264" => (new OpaqueH264Packetiser(), new OpaqueH264Depacketiser(maxEncodedFrameBytes)),
            _ => throw new InvalidOperationException(
                $"Negotiated video codec '{codecName}' has no opaque RTP payload format implementation."),
        };
    }
}
