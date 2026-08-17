namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// H.264 RTP depacketiser for frames the SDK must not read (#223): it reassembles from the FU-A indicator and
/// FU header alone (RFC 6184 §5.8) and never interprets a byte of the fragment payload.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="H264Depacketiser"/> is deliberately fail-closed on payload semantics — it dispatches on the NAL
/// type, parses STAP-A aggregation sizes, derives the key-frame flag from an IDR NAL and emits Annex-B start
/// codes. Every one of those is a statement about the frame's content, so an opaque frame is malformed by
/// definition and nothing arrives at all. This implementation reads only what its counterpart
/// <see cref="OpaqueH264Packetiser"/> wrote: the FU-A type in the indicator, and S/E in the FU header. The
/// output is the concatenated fragment payloads, verbatim — no start codes, no reconstructed NAL header byte,
/// no key-frame claim (<c>isKeyFrame</c> is always <see langword="false"/>; that signal belongs in a plaintext
/// header extension, the follow-up work #223 points at).
/// </para>
/// <para>
/// Only FU-A is accepted. Single-NAL and STAP-A packets are refused (counted as discards) rather than guessed
/// at: for opaque data their leading byte is content, and treating content as a header is exactly the class of
/// bug this path removes. That makes this depacketiser the counterpart of the opaque packetiser and of a relay
/// forwarding its packets — a browser peer, whose H.264 framing keeps NAL headers in the clear, is served by
/// the non-opaque path (see ADR-068).
/// </para>
/// <para>
/// Stateful per stream and <b>not thread-safe</b>, with the same reassembly cap and discard semantics as the
/// non-opaque path (K4).
/// </para>
/// </remarks>
internal sealed class OpaqueH264Depacketiser : IVideoDepacketiser
{
    private const int FuATypeCode = 28;
    private const int FuAHeaderLength = 2;

    // Above this retained capacity a reassembly buffer is released on Reset so a single large frame cannot
    // permanently pin memory per track/RID lane.
    private const int RetainCapacityBytes = 256 * 1024;

    private readonly MemoryStream _frame = new();
    private readonly int _maxFrameBytes;
    private bool _frameActive;
    private uint _timestamp;

    /// <summary>Creates the depacketiser with a hard reassembly cap (K4).</summary>
    public OpaqueH264Depacketiser(int maxFrameBytes = VideoPayloadFormat.DefaultMaxEncodedFrameBytes)
    {
        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), "Max frame size must be positive.");
        _maxFrameBytes = maxFrameBytes;
    }

    /// <inheritdoc />
    /// <remarks>Never reads the payload, so it never claims a key frame (#223, #310).</remarks>
    public bool DerivesKeyFrameFromPayload => false;

    /// <inheritdoc />
    public long DiscardedPacketCount { get; private set; }

    /// <inheritdoc />
    public long OversizedFrameDiscardCount { get; private set; }

    /// <inheritdoc />
    /// <remarks><paramref name="isKeyFrame"/> is always <see langword="false"/> — see the type remarks.</remarks>
    public bool TryProcess(ReadOnlyMemory<byte> rtpPayload, uint rtpTimestamp, bool marker, out byte[]? frame, out bool isKeyFrame)
    {
        frame = null;
        isKeyFrame = false;

        // A timestamp change without a closing marker means the sender started the next frame (markerless
        // senders exist) — the half frame must never merge into it.
        if (rtpTimestamp != _timestamp)
        {
            Reset();
            _timestamp = rtpTimestamp;
        }

        var payload = rtpPayload.Span;
        if (payload.Length < FuAHeaderLength + 1)
            return Discard();

        // The only two things read: the indicator's type field, and S/E in the FU header.
        if ((payload[0] & 0x1F) != FuATypeCode)
            return Discard();

        var fuHeader = payload[1];
        var start = (fuHeader & 0x80) != 0;
        var end = (fuHeader & 0x40) != 0;

        if (start)
        {
            // S=1 inside an open run restarts assembly: the partial frame is dropped and the new one assembles
            // cleanly (libwebrtc behaviour, and what Vp8Depacketiser does on a mid-frame frame start).
            _frame.SetLength(0);
            _frameActive = true;
        }
        else if (!_frameActive)
        {
            DiscardedPacketCount++;
            return false; // continuation of a frame whose start we never saw — drop
        }

        // K4: bound reassembly. A never-terminated fragment run cannot grow past the cap — over it the whole
        // frame under assembly is discarded, so it never pins memory or desyncs the next frame.
        if (_frame.Length + (payload.Length - FuAHeaderLength) > _maxFrameBytes)
        {
            OversizedFrameDiscardCount++;
            return Discard();
        }

        _frame.Write(payload[FuAHeaderLength..]);

        // E is the authoritative frame boundary for this framing — the fragment run declares its own end, and on
        // the matching packetiser E and the RTP marker are set on the same packet. Not requiring the marker in
        // addition keeps a markerless sender working, exactly as the timestamp-change reset above does.
        if (!end)
            return false;

        _frameActive = false;
        frame = _frame.ToArray();
        _frame.SetLength(0);
        return frame.Length > 0;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _frame.SetLength(0);
        // Release an over-grown buffer (its length is already 0) so a one-off large frame cannot pin memory.
        if (_frame.Capacity > RetainCapacityBytes)
            _frame.Capacity = 0;
        _frameActive = false;
    }

    private bool Discard()
    {
        DiscardedPacketCount++;
        Reset();
        return false;
    }
}
