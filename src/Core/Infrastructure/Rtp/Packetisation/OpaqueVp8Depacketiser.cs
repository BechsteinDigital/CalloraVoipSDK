namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// VP8 RTP depacketiser for frames the SDK must not read (#223): it reassembles from the payload descriptor
/// alone (RFC 7741 §4.2) and never touches a byte of the frame data behind it.
/// </summary>
/// <remarks>
/// <para>
/// The difference to <see cref="Vp8Depacketiser"/> is one thing and it is the point: that one derives the
/// key-frame flag from the VP8 payload header (RFC 7741 §4.3 → RFC 6386 §9.1), which is the first byte of the
/// <em>frame</em>. Under WebRTC Encoded Transform / SFrame (RFC 9605) that byte is ciphertext, so the flag
/// becomes noise — wrong key-frame detection, PLI storms, participants without a picture. This implementation
/// reports <c>isKeyFrame: false</c> unconditionally instead of guessing: the flag belongs in a plaintext RTP
/// header extension (Dependency Descriptor), which is the follow-up work #223 points at.
/// </para>
/// <para>
/// The descriptor stays readable because a sender generates it from encoder metadata, not by parsing the frame
/// — that is what makes descriptor-only reassembly possible at all, and it is the same property a real E2EE
/// sender relies on (Jitsi leaves the first 3/10 payload bytes in the clear for exactly this reason; libwebrtc's
/// frame cryptor keeps per-codec "unencrypted header bytes"). This depacketiser needs none of them.
/// </para>
/// <para>
/// Stateful per stream and <b>not thread-safe</b>, with the same reassembly cap and discard semantics as the
/// non-opaque path (K4).
/// </para>
/// </remarks>
internal sealed class OpaqueVp8Depacketiser : IVideoDepacketiser
{
    // Above this retained capacity the reassembly buffer is released on Reset so a single large frame cannot
    // permanently pin memory per track/RID lane.
    private const int RetainCapacityBytes = 256 * 1024;

    private readonly MemoryStream _frame = new();
    private readonly int _maxFrameBytes;
    private bool _frameActive;
    private uint _timestamp;

    /// <summary>Creates the depacketiser with a hard reassembly cap (K4).</summary>
    public OpaqueVp8Depacketiser(int maxFrameBytes = VideoPayloadFormat.DefaultMaxEncodedFrameBytes)
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

        // Frame boundary without a marker (markerless senders): never merge the half frame into the next one.
        if (rtpTimestamp != _timestamp)
        {
            Reset();
            _timestamp = rtpTimestamp;
        }

        var payload = rtpPayload.Span;

        if (!Vp8PayloadDescriptor.TryStrip(payload, out var headerLength, out var isFrameStart))
            return Discard();

        if (isFrameStart)
        {
            _frame.SetLength(0);
            _frameActive = true;
        }
        else if (!_frameActive)
        {
            DiscardedPacketCount++;
            return false; // continuation of a frame whose start we never saw — drop
        }

        // K4: bound reassembly. A same-timestamp run with no marker cannot grow past the cap.
        if (_frame.Length + (payload.Length - headerLength) > _maxFrameBytes)
        {
            OversizedFrameDiscardCount++;
            return Discard();
        }

        _frame.Write(payload[headerLength..]);

        if (!marker)
            return false;

        frame = _frame.ToArray();
        _frame.SetLength(0);
        _frameActive = false;
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
