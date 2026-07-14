namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// Splits one encoded video frame into RTP payloads honouring the payload-size budget
/// (RTP payload formats: H.264 RFC 6184, VP8 RFC 7741). Stateless — one instance can
/// serve any number of streams.
/// </summary>
internal interface IVideoPacketiser
{
    /// <summary>
    /// Packetises one encoded frame (for H.264: one Annex-B access unit). Every returned
    /// payload fits <paramref name="maxPayloadSize"/>; exactly the last one carries
    /// <see cref="VideoRtpPayload.IsLastOfFrame"/>.
    /// </summary>
    /// <param name="encodedFrame">
    /// The encoder output for one complete frame. Returned payloads may reference this
    /// memory (zero-copy) — do not reuse or pool the buffer until they are sent.
    /// </param>
    /// <param name="maxPayloadSize">RTP payload budget (MTU minus RTP/SRTP overhead).</param>
    /// <exception cref="ArgumentException">The frame is empty or malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPayloadSize"/> is too small.</exception>
    IReadOnlyList<VideoRtpPayload> Packetise(ReadOnlyMemory<byte> encodedFrame, int maxPayloadSize);
}
