using System.Buffers.Binary;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

/// <summary>
/// H.264 RTP depacketiser (RFC 6184): reassembles Annex-B access units from Single NAL
/// Unit packets (§5.6), STAP-A aggregations (§5.7.1 — browsers commonly bundle SPS/PPS
/// this way), and FU-A fragments (§5.8). Unsupported packetisation modes (STAP-B,
/// MTAP, FU-B) and malformed payloads discard the frame under assembly — fail closed,
/// never a corrupted access unit.
/// </summary>
internal sealed class H264Depacketiser : IVideoDepacketiser
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    // NAL unit type 5 = coded slice of an IDR picture (RFC 6184 / H.264 §7.4.1) — its
    // presence in the access unit marks a key frame.
    private const int IdrNalType = 5;

    // Above this retained capacity a reassembly buffer is released on Reset so a single large frame cannot
    // permanently pin memory per track/RID lane; a typical coded frame is well under this.
    private const int RetainCapacityBytes = 256 * 1024;

    private readonly MemoryStream _frame = new();
    private readonly MemoryStream _fragment = new();
    private readonly int _maxFrameBytes;
    private bool _fragmentActive;
    private bool _isKeyFrame;
    private uint _timestamp;

    /// <summary>Creates the depacketiser with a hard reassembly cap (K4).</summary>
    public H264Depacketiser(int maxFrameBytes = VideoPayloadFormat.DefaultMaxEncodedFrameBytes)
    {
        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), "Max frame size must be positive.");
        _maxFrameBytes = maxFrameBytes;
    }

    /// <inheritdoc />
    /// <remarks>Reads the payload, so the answer is only as readable as the payload is (#310).</remarks>
    public bool DerivesKeyFrameFromPayload => true;

    /// <inheritdoc />
    public long DiscardedPacketCount { get; private set; }

    /// <inheritdoc />
    public long OversizedFrameDiscardCount { get; private set; }

    /// <inheritdoc />
    public bool TryProcess(ReadOnlyMemory<byte> rtpPayload, uint rtpTimestamp, bool marker, out byte[]? frame, out bool isKeyFrame)
    {
        frame = null;
        isKeyFrame = false;

        // A timestamp change without a closing marker means the sender started the next
        // access unit (markerless senders exist) — the half frame must never merge into it.
        if (rtpTimestamp != _timestamp)
        {
            Reset();
            _timestamp = rtpTimestamp;
        }

        var payload = rtpPayload.Span;
        if (payload.Length < 1)
            return Discard();

        // The forbidden_zero_bit (F, §5.3) is tolerated — receivers MAY discard, decoders
        // handle syntax violations themselves.
        var nalType = payload[0] & 0x1F;
        var accepted = nalType switch
        {
            >= 1 and <= 23 => AppendNal(payload),
            24 => AppendStapA(payload),
            28 => AppendFuA(payload),
            _ => false, // STAP-B/MTAP/FU-B (§5.2) not supported
        };

        if (!accepted)
            return Discard();

        if (!marker)
            return false;

        if (_fragmentActive)
            return Discard(); // marker inside an open FU-A run — truncated fragment

        if (_frame.Length == 0)
            return false;

        frame = _frame.ToArray();
        isKeyFrame = _isKeyFrame;
        _frame.SetLength(0);
        _isKeyFrame = false;
        return true;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _frame.SetLength(0);
        _fragment.SetLength(0);
        ShrinkIfOversized(_frame);
        ShrinkIfOversized(_fragment);
        _fragmentActive = false;
        _isKeyFrame = false;
    }

    private bool Discard()
    {
        DiscardedPacketCount++;
        Reset();
        return false;
    }

    // K4: refuse a write that would push the reassembly buffer past the cap. The caller returns false and
    // TryProcess then discards the whole frame (Reset), so it never grows without bound or desyncs the next.
    private bool WithinCap(MemoryStream target, int addLength)
    {
        if (target.Length + addLength <= _maxFrameBytes)
            return true;
        OversizedFrameDiscardCount++;
        return false;
    }

    // Release an over-grown buffer (its length is already 0) so a one-off large frame cannot pin memory.
    private static void ShrinkIfOversized(MemoryStream stream)
    {
        if (stream.Capacity > RetainCapacityBytes)
            stream.Capacity = 0;
    }

    private bool AppendNal(ReadOnlySpan<byte> nal)
    {
        if (_fragmentActive)
            return false; // a new NAL inside an open FU-A run means lost fragments

        if (!WithinCap(_frame, StartCode.Length + nal.Length))
            return false;

        if ((nal[0] & 0x1F) == IdrNalType)
            _isKeyFrame = true;

        _frame.Write(StartCode);
        _frame.Write(nal);
        return true;
    }

    // STAP-A (§5.7.1): payload = STAP-A NAL header, then per unit a 16-bit size + NAL.
    private bool AppendStapA(ReadOnlySpan<byte> payload)
    {
        if (_fragmentActive)
            return false;

        var offset = 1;
        while (offset < payload.Length)
        {
            if (offset + 2 > payload.Length)
                return false;

            var size = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            if (size == 0 || offset + size > payload.Length)
                return false;

            if (!WithinCap(_frame, StartCode.Length + size))
                return false;

            if ((payload[offset] & 0x1F) == IdrNalType)
                _isKeyFrame = true;

            _frame.Write(StartCode);
            _frame.Write(payload.Slice(offset, size));
            offset += size;
        }

        return offset > 1; // RFC 6184 §5.7.1: a STAP-A must carry at least one unit
    }

    // FU-A (§5.8): indicator + FU header; S starts a fragment run, E ends it; the
    // original NAL header is reconstructed from indicator NRI + FU header type.
    private bool AppendFuA(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            return false;

        var fuHeader = payload[1];
        var start = (fuHeader & 0x80) != 0;
        var end = (fuHeader & 0x40) != 0;

        if (start)
        {
            if (_fragmentActive)
                return false; // S=1 inside an open run — protocol violation, fail closed

            if ((fuHeader & 0x1F) == IdrNalType)
                _isKeyFrame = true;

            _fragment.SetLength(0);
            _fragment.WriteByte((byte)((payload[0] & 0xE0) | (fuHeader & 0x1F)));
            _fragmentActive = true;
        }
        else if (!_fragmentActive)
        {
            return false; // continuation without a start — the first fragment was lost
        }

        if (!WithinCap(_fragment, payload.Length - 2))
            return false; // never-terminated FU-A run — bounded then discarded (fail closed)
        _fragment.Write(payload[2..]);

        if (end)
        {
            if (!WithinCap(_frame, StartCode.Length + (int)_fragment.Length))
                return false;
            _frame.Write(StartCode);
            _frame.Write(_fragment.GetBuffer().AsSpan(0, (int)_fragment.Length));
            _fragment.SetLength(0);
            _fragmentActive = false;
        }

        return true;
    }
}
