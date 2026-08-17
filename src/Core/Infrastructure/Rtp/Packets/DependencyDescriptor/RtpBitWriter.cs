namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Big-endian bit writer, the counterpart to <see cref="RtpBitReader"/>, for producing the Dependency
/// Descriptor's bit-packed syntax (AV1 RTP specification §A).
/// </summary>
/// <remarks>
/// Grows into a fixed buffer sized by the caller: descriptors are small (a mandatory-only one is three
/// bytes, a structure-carrying one for a single-layer stream a handful more), so the writer never needs to
/// resize on the send path.
/// </remarks>
internal sealed class RtpBitWriter(int capacityBytes)
{
    private readonly byte[] _buffer = new byte[capacityBytes];
    private int _bitPosition;

    /// <summary>Bytes written so far, rounded up to the next whole byte (the trailing bits are zero).</summary>
    public int ByteLength => (_bitPosition + 7) / 8;

    /// <summary>Writes <paramref name="bitCount"/> bits (0..32) of <paramref name="value"/>, most significant first.</summary>
    public void Write(uint value, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be 0..32.");
        if (_bitPosition + bitCount > _buffer.Length * 8)
            throw new InvalidOperationException("Dependency descriptor exceeds the buffer reserved for it.");

        for (var i = bitCount - 1; i >= 0; i--)
        {
            var bit = (value >> i) & 1;
            if (bit != 0)
            {
                var byteIndex = _bitPosition >> 3;
                var bitIndex = 7 - (_bitPosition & 7);
                _buffer[byteIndex] |= (byte)(1 << bitIndex);
            }

            _bitPosition++;
        }
    }

    /// <summary>Writes a single flag bit.</summary>
    public void WriteFlag(bool value) => Write(value ? 1u : 0u, 1);

    /// <summary>
    /// Writes the non-symmetric unsigned integer <c>ns(n)</c> (AV1 §4.10.7) — the inverse of
    /// <see cref="RtpBitReader.ReadNonSymmetric"/>.
    /// </summary>
    public void WriteNonSymmetric(uint value, uint n)
    {
        if (n <= 1)
            return;

        var w = 0;
        var x = n;
        while (x != 0)
        {
            x >>= 1;
            w++;
        }

        var m = (uint)((1 << w) - (int)n);
        if (value < m)
        {
            Write(value, w - 1);
            return;
        }

        var shifted = value + m;
        Write(shifted >> 1, w - 1);
        Write(shifted & 1, 1);
    }

    /// <summary>The written bytes, zero-padded in the final byte.</summary>
    public byte[] ToArray() => _buffer[..ByteLength];
}
