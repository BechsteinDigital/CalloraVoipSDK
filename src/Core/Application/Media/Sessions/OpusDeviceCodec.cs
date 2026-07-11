namespace CalloraVoipSdk.Core.Application.Media.Sessions;

/// <summary>
/// Reusable Opus (RFC 7587) codec adapter for platform audio backends. Wraps a single
/// <see cref="OpusPayloadCodec"/> stream (mono, 48 kHz) and adds an encode accumulator: audio
/// device capture callbacks deliver PCM blocks that may not line up with a valid Opus frame size,
/// so captured PCM is buffered and emitted as complete 20 ms frames (960 samples at 48 kHz).
/// Inbound decode is a straight pass-through.
/// <para>Instance-per-call, per full-duplex stream: <see cref="Decode"/> runs on the inbound media
/// thread and <see cref="Encode"/> on the capture thread; they touch disjoint state (the Concentus
/// decoder vs. the encoder plus the backlog), so no locking is required as long as each direction
/// stays single-threaded — the contract every device backend already honours for its codec state.</para>
/// </summary>
internal sealed class OpusDeviceCodec
{
    /// <summary>Samples in one emitted Opus frame (20 ms at 48 kHz); also the RTP timestamp units per frame.</summary>
    public const int FrameSamples = OpusPayloadCodec.SamplesPerDefaultFrame;

    private const int FrameBytes = FrameSamples * 2;

    private readonly OpusPayloadCodec _codec = new(OpusPayloadCodec.RtpClockRate);
    private readonly List<byte> _encodeBacklog = new();

    /// <summary>Decodes one inbound Opus payload into PCM16 little-endian bytes (mono, 48 kHz).</summary>
    public byte[] Decode(ReadOnlySpan<byte> opusPayload) => _codec.Decode(opusPayload);

    /// <summary>
    /// Appends captured PCM16 (mono, 48 kHz) and returns zero or more Opus payloads for every whole
    /// 20 ms frame now available. Sub-frame remainders are buffered for the next call; a half-sample
    /// tail (odd byte count) is dropped to preserve sample alignment rather than throw on the audio
    /// callback thread.
    /// </summary>
    public IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> pcm16)
    {
        var length = pcm16.Length;
        if ((length & 1) != 0)
            length--;

        if (length > 0)
            _encodeBacklog.AddRange(pcm16[..length].ToArray());

        if (_encodeBacklog.Count < FrameBytes)
            return [];

        var packets = new List<byte[]>();
        var frame = new byte[FrameBytes];
        while (_encodeBacklog.Count >= FrameBytes)
        {
            _encodeBacklog.CopyTo(0, frame, 0, FrameBytes);
            _encodeBacklog.RemoveRange(0, FrameBytes);
            packets.Add(_codec.Encode(frame));
        }

        return packets;
    }
}
