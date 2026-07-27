using System.Buffers.Binary;

namespace MiniCore.Compare.Interop.Adapters;

internal static class SipSorceryPcmuWaveCodec
{
    public const int SampleRate = 8000;
    public const int SamplesPerFrame = 160;
    private const int WaveHeaderSize = 44;

    public static async Task<short[]> ReadPcm16Async(
        string path,
        CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        ValidateCanonicalHeader(bytes);

        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        if (dataLength < 0 || WaveHeaderSize + dataLength > bytes.Length || (dataLength & 1) != 0)
        {
            throw new InvalidDataException("Invalid canonical PCM16 WAV data length.");
        }

        var samples = new short[dataLength / sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(WaveHeaderSize + (i * sizeof(short)), sizeof(short)));
        }

        return samples;
    }

    public static async Task WritePcm16Async(
        string path,
        ReadOnlyMemory<short> samples,
        CancellationToken ct = default)
    {
        var dataLength = checked(samples.Length * sizeof(short));
        var bytes = new byte[WaveHeaderSize + dataLength];
        WriteAscii(bytes, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 36 + dataLength);
        WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), SampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        WriteAscii(bytes, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataLength);

        var source = samples.Span;
        for (var i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(WaveHeaderSize + (i * sizeof(short)), sizeof(short)),
                source[i]);
        }

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public static byte EncodeMuLaw(short pcmSample)
    {
        const int bias = 0x84;
        const int clip = 32635;

        var sample = (int)pcmSample;
        var sign = sample < 0 ? 0x80 : 0;
        if (sample < 0)
        {
            sample = -sample;
        }

        sample = Math.Min(sample, clip) + bias;

        var exponent = 7;
        for (var mask = 0x4000; exponent > 0 && (sample & mask) == 0; exponent--, mask >>= 1)
        {
        }

        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short DecodeMuLaw(byte muLaw)
    {
        var encoded = (byte)~muLaw;
        var sign = encoded & 0x80;
        var exponent = (encoded >> 4) & 0x07;
        var mantissa = encoded & 0x0F;
        var sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign == 0 ? sample : -sample);
    }

    private static void ValidateCanonicalHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < WaveHeaderSize
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)
            || !bytes.Slice(12, 4).SequenceEqual("fmt "u8)
            || !bytes.Slice(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(20, 2)) != 1
            || BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(22, 2)) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)) != SampleRate
            || BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(34, 2)) != 16)
        {
            throw new InvalidDataException("Expected canonical mono 8 kHz PCM16 WAV.");
        }
    }

    private static void WriteAscii(Span<byte> destination, int offset, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            destination[offset + i] = checked((byte)value[i]);
        }
    }
}
