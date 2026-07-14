namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// VP8 RTP packetiser (RFC 7741): prefixes each fragment of one encoded VP8 frame with
/// the minimal one-byte payload descriptor (§4.2) — S set on the first packet, partition
/// index 0, no extensions. Extension fields (picture ID etc.) are a follow-up once the
/// codec layer needs them; the depacketiser already skips them on receive.
/// </summary>
internal sealed class Vp8Packetiser : IVideoPacketiser
{
    private const int DescriptorLength = 1;
    private const byte StartOfPartitionDescriptor = 0x10; // X=0, N=0, S=1, PID=0
    private const byte ContinuationDescriptor = 0x00;

    /// <inheritdoc />
    public IReadOnlyList<VideoRtpPayload> Packetise(ReadOnlyMemory<byte> encodedFrame, int maxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, DescriptorLength + 1);
        if (encodedFrame.IsEmpty)
            throw new ArgumentException("VP8 frame is empty.", nameof(encodedFrame));

        var fragmentBudget = maxPayloadSize - DescriptorLength;
        var payloads = new List<VideoRtpPayload>();
        var remaining = encodedFrame;
        var isFirst = true;

        while (remaining.Length > 0)
        {
            var take = Math.Min(fragmentBudget, remaining.Length);
            var payload = new byte[DescriptorLength + take];
            payload[0] = isFirst ? StartOfPartitionDescriptor : ContinuationDescriptor;
            remaining.Span[..take].CopyTo(payload.AsSpan(DescriptorLength));

            payloads.Add(new VideoRtpPayload
            {
                Payload = payload,
                IsLastOfFrame = take == remaining.Length,
            });

            remaining = remaining[take..];
            isFirst = false;
        }

        return payloads;
    }
}
