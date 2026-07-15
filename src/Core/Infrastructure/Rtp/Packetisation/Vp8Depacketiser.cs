namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// VP8 RTP depacketiser (RFC 7741): strips the payload descriptor — including the
/// optional extension fields peers commonly send (picture ID, TL0PICIDX, TID/KEYIDX,
/// §4.2) — and reassembles the encoded frame. A frame must open with an S=1/PID=0
/// packet; anything else while no frame is assembling is discarded (lost frame start).
/// An S=1/PID=0 packet arriving mid-frame restarts assembly (libwebrtc behaviour: the
/// partial frame is dropped, the new one assembles cleanly).
/// </summary>
internal sealed class Vp8Depacketiser : IVideoDepacketiser
{
    private readonly MemoryStream _frame = new();
    private bool _frameActive;
    private bool _isKeyFrame;
    private uint _timestamp;

    /// <inheritdoc />
    public bool TryProcess(ReadOnlyMemory<byte> rtpPayload, uint rtpTimestamp, bool marker, out byte[]? frame, out bool isKeyFrame)
    {
        frame = null;
        isKeyFrame = false;

        // Frame boundary without a marker (markerless senders): never merge the half
        // frame into the next one.
        if (rtpTimestamp != _timestamp)
        {
            Reset();
            _timestamp = rtpTimestamp;
        }

        var payload = rtpPayload.Span;

        if (!TryStripDescriptor(payload, out var headerLength, out var isFrameStart))
            return Discard();

        if (isFrameStart)
        {
            _frame.SetLength(0);
            _frameActive = true;
            // First byte of the VP8 payload header (RFC 7741 §4.3 → RFC 6386 §9.1): bit 0
            // is P (inverse key-frame flag); P=0 marks a key frame.
            _isKeyFrame = (payload[headerLength] & 0x01) == 0;
        }
        else if (!_frameActive)
        {
            return false; // continuation of a frame whose start we never saw — drop
        }

        _frame.Write(payload[headerLength..]);

        if (!marker)
            return false;

        frame = _frame.ToArray();
        isKeyFrame = _isKeyFrame;
        _frame.SetLength(0);
        _frameActive = false;
        return frame.Length > 0;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _frame.SetLength(0);
        _frameActive = false;
        _isKeyFrame = false;
    }

    private bool Discard()
    {
        Reset();
        return false;
    }

    // Payload descriptor (§4.2): mandatory first byte |X|R|N|S|R|PID|, optionally
    // followed by X-byte |I|L|T|K|…, picture ID (1-2 bytes), TL0PICIDX, TID/KEYIDX.
    private static bool TryStripDescriptor(ReadOnlySpan<byte> payload, out int headerLength, out bool isFrameStart)
    {
        headerLength = 0;
        isFrameStart = false;
        if (payload.Length < 2)
            return false; // descriptor plus at least one payload byte

        var b0 = payload[0];
        isFrameStart = (b0 & 0x10) != 0 && (b0 & 0x07) == 0; // S=1, PID=0
        headerLength = 1;

        if ((b0 & 0x80) == 0)
            return payload.Length > headerLength;

        if (payload.Length <= headerLength)
            return false;
        var extension = payload[headerLength++];

        if ((extension & 0x80) != 0) // I: picture ID, 15-bit form when the M bit is set
        {
            if (payload.Length <= headerLength)
                return false;
            headerLength += (payload[headerLength] & 0x80) != 0 ? 2 : 1;
        }

        if ((extension & 0x40) != 0) // L: TL0PICIDX
            headerLength++;

        if ((extension & 0x30) != 0) // T and/or K: shared TID/Y/KEYIDX byte
            headerLength++;

        return payload.Length > headerLength;
    }
}
