namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// H.264 RTP packetiser (RFC 6184): splits one Annex-B access unit into RTP payloads.
/// NAL units within the payload budget travel as Single NAL Unit packets (§5.6); larger
/// ones are fragmented as FU-A (§5.8). STAP-A aggregation on the send path is not
/// emitted (optional per §5.2); the depacketiser accepts it from peers.
/// </summary>
internal sealed class H264Packetiser : IVideoPacketiser
{
    // FU-A overhead: 1 byte FU indicator + 1 byte FU header (§5.8).
    private const int FuAHeaderLength = 2;
    private const byte FuATypeCode = 28;

    /// <inheritdoc />
    public IReadOnlyList<VideoRtpPayload> Packetise(ReadOnlyMemory<byte> encodedFrame, int maxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, FuAHeaderLength + 1);

        var nalUnits = AnnexBParser.ParseNalUnits(encodedFrame);
        if (nalUnits.Count == 0)
            throw new ArgumentException("Frame contains no Annex-B NAL units.", nameof(encodedFrame));

        var payloads = new List<VideoRtpPayload>();
        for (var i = 0; i < nalUnits.Count; i++)
        {
            var isLastNal = i == nalUnits.Count - 1;
            var nal = nalUnits[i];

            if (nal.Length <= maxPayloadSize)
            {
                payloads.Add(new VideoRtpPayload { Payload = nal, IsLastOfFrame = isLastNal });
                continue;
            }

            AppendFuAFragments(payloads, nal, maxPayloadSize, isLastNal);
        }

        return payloads;
    }

    /// <summary>
    /// Fragments one NAL unit as FU-A (§5.8): the NAL header byte is not transmitted —
    /// its NRI travels in the FU indicator, its type in the FU header; S/E flag the
    /// first/last fragment.
    /// </summary>
    private static void AppendFuAFragments(
        List<VideoRtpPayload> payloads, ReadOnlyMemory<byte> nal, int maxPayloadSize, bool isLastNal)
    {
        var nalHeader = nal.Span[0];
        var fuIndicator = (byte)((nalHeader & 0xE0) | FuATypeCode);
        var nalType = (byte)(nalHeader & 0x1F);

        var remaining = nal[1..];
        var fragmentBudget = maxPayloadSize - FuAHeaderLength;
        var isFirst = true;

        while (remaining.Length > 0)
        {
            var take = Math.Min(fragmentBudget, remaining.Length);
            var isLastFragment = take == remaining.Length;

            var payload = new byte[FuAHeaderLength + take];
            payload[0] = fuIndicator;
            payload[1] = (byte)((isFirst ? 0x80 : 0x00) | (isLastFragment ? 0x40 : 0x00) | nalType);
            remaining.Span[..take].CopyTo(payload.AsSpan(FuAHeaderLength));

            payloads.Add(new VideoRtpPayload
            {
                Payload = payload,
                IsLastOfFrame = isLastNal && isLastFragment,
            });

            remaining = remaining[take..];
            isFirst = false;
        }
    }
}
