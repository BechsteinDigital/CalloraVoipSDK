namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// Big-endian bit reader for the Dependency Descriptor's bit-packed syntax (AV1 RTP specification §A):
/// fields are <c>f(n)</c> reads that do not align to byte boundaries, plus the non-symmetric unsigned
/// <c>ns(n)</c> encoding.
/// </summary>
/// <remarks>
/// Every read is bounds-checked and reports exhaustion rather than throwing: the input is a header
/// extension from a remote peer, so a truncated or hostile descriptor must end the parse, not the packet
/// (K4). A ref struct so it cannot outlive the span it reads.
/// </remarks>
internal ref struct RtpBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPosition;

    /// <summary>Bits not yet consumed.</summary>
    public readonly int RemainingBits => (_data.Length * 8) - _bitPosition;

    /// <summary>Whether the reader ran past the end of the buffer at any point.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits (0..32) as an unsigned big-endian value — the spec's
    /// <c>f(n)</c>. Sets <see cref="Exhausted"/> and returns 0 when the buffer is too short.
    /// </summary>
    public uint Read(int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be 0..32.");
        if (bitCount == 0)
            return 0;
        if (Exhausted || RemainingBits < bitCount)
        {
            Exhausted = true;
            return 0;
        }

        uint value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var byteIndex = _bitPosition >> 3;
            var bitIndex = 7 - (_bitPosition & 7); // most significant bit first
            var bit = (_data[byteIndex] >> bitIndex) & 1;
            value = (value << 1) | (uint)bit;
            _bitPosition++;
        }

        return value;
    }

    /// <summary>Reads a single bit as a flag — <c>f(1)</c>.</summary>
    public bool ReadFlag() => Read(1) != 0;

    /// <summary>
    /// Reads the non-symmetric unsigned integer <c>ns(n)</c> (AV1 §4.10.7): values 0..n-1 in
    /// <c>FloorLog2(n)</c> or <c>FloorLog2(n)+1</c> bits, whichever the value needs.
    /// </summary>
    public uint ReadNonSymmetric(uint n)
    {
        if (n <= 1)
            return 0;

        var w = 0;
        var x = n;
        while (x != 0)
        {
            x >>= 1;
            w++;
        }

        var m = (uint)((1 << w) - (int)n);
        var v = Read(w - 1);
        if (v < m)
            return v;

        var extraBit = Read(1);
        return (v << 1) - m + extraBit;
    }
}
