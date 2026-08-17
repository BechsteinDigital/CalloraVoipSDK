namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// H.264 RTP packetiser for frames the SDK must not read (#223): it fragments the frame by size alone, as
/// FU-A (RFC 6184 §5.8), and never parses it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="H264Packetiser"/> cannot serve an opaque frame: it runs the Annex-B parser to find NAL
/// boundaries, and ciphertext has none — it would either throw or split on byte patterns that mean nothing.
/// This packetiser instead <b>synthesises</b> the NAL header RFC 6184 requires in every packet (F=0, NRI=3,
/// non-IDR type 1) and carries the frame verbatim as fragment payload, so no byte of the frame is read,
/// interpreted or altered. The matching <see cref="OpaqueH264Depacketiser"/> strips that synthetic framing
/// again, which makes the round trip byte-identical for arbitrary content.
/// </para>
/// <para>
/// Every frame is fragmented, including one that would fit a single packet. RFC 6184 §5.8 only discourages
/// fragmenting a small NAL unit (SHOULD NOT, not MUST NOT), and always doing it removes a hazard that matters
/// precisely for opaque data: a single-NAL packet would put the frame's first byte where a receiver reads the
/// NAL type, so ciphertext beginning with 0x1C or 0x18 would be mistaken for an FU-A or STAP-A packet. With
/// FU-A throughout, every packet's type field is ours, and the depacketiser never has to guess.
/// </para>
/// <para>
/// <b>Interop scope.</b> This framing is self-consistent between two SDK endpoints and through a relay that
/// forwards payloads untouched. It is not what a browser emits: Chrome's and Firefox's H.264 packetisers run
/// after the Encoded Transform and still expect Annex-B structure, which is why every shipping E2EE
/// implementation (Jitsi, LiveKit/libwebrtc frame cryptor) leaves the NAL headers and start codes in the clear
/// and RBSP-escapes the ciphertext instead. Receiving from such a peer is the non-opaque path's job — see
/// ADR-068 and the follow-up work referenced there.
/// </para>
/// <para>Stateless — one instance can serve any number of streams.</para>
/// </remarks>
internal sealed class OpaqueH264Packetiser : IVideoPacketiser
{
    // FU-A overhead: 1 byte FU indicator + 1 byte FU header (§5.8).
    private const int FuAHeaderLength = 2;

    // Synthetic FU indicator: F=0, NRI=3 (the highest importance — an opaque frame's importance is unknown and
    // must not be understated), type 28 = FU-A.
    private const byte FuIndicator = 0x60 | 28;

    // Synthetic fragmented-NAL type carried in the FU header: 1 = coded slice of a non-IDR picture. Deliberately
    // NOT 5 (IDR): the SDK cannot know whether an opaque frame is a key frame, and claiming so would feed the
    // same wrong flag the opaque path exists to remove.
    private const byte FragmentedNalType = 1;

    /// <inheritdoc />
    public IReadOnlyList<VideoRtpPayload> Packetise(ReadOnlyMemory<byte> encodedFrame, int maxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, FuAHeaderLength + 1);
        if (encodedFrame.IsEmpty)
            throw new ArgumentException("Opaque H.264 frame is empty.", nameof(encodedFrame));

        var fragmentBudget = maxPayloadSize - FuAHeaderLength;
        var payloads = new List<VideoRtpPayload>();
        var remaining = encodedFrame;
        var isFirst = true;

        while (remaining.Length > 0)
        {
            var take = Math.Min(fragmentBudget, remaining.Length);
            var isLast = take == remaining.Length;

            var payload = new byte[FuAHeaderLength + take];
            payload[0] = FuIndicator;
            payload[1] = (byte)((isFirst ? 0x80 : 0x00) | (isLast ? 0x40 : 0x00) | FragmentedNalType);
            remaining.Span[..take].CopyTo(payload.AsSpan(FuAHeaderLength));

            payloads.Add(new VideoRtpPayload
            {
                Payload = payload,
                IsLastOfFrame = isLast,
            });

            remaining = remaining[take..];
            isFirst = false;
        }

        return payloads;
    }
}
