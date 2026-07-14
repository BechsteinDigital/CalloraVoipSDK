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

    private readonly MemoryStream _frame = new();
    private readonly MemoryStream _fragment = new();
    private bool _fragmentActive;
    private uint _timestamp;

    /// <inheritdoc />
    public bool TryProcess(ReadOnlyMemory<byte> rtpPayload, uint rtpTimestamp, bool marker, out byte[]? frame)
    {
        frame = null;

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
        _frame.SetLength(0);
        return true;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _frame.SetLength(0);
        _fragment.SetLength(0);
        _fragmentActive = false;
    }

    private bool Discard()
    {
        Reset();
        return false;
    }

    private bool AppendNal(ReadOnlySpan<byte> nal)
    {
        if (_fragmentActive)
            return false; // a new NAL inside an open FU-A run means lost fragments

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

            _fragment.SetLength(0);
            _fragment.WriteByte((byte)((payload[0] & 0xE0) | (fuHeader & 0x1F)));
            _fragmentActive = true;
        }
        else if (!_fragmentActive)
        {
            return false; // continuation without a start — the first fragment was lost
        }

        _fragment.Write(payload[2..]);

        if (end)
        {
            _frame.Write(StartCode);
            _frame.Write(_fragment.GetBuffer().AsSpan(0, (int)_fragment.Length));
            _fragment.SetLength(0);
            _fragmentActive = false;
        }

        return true;
    }
}
