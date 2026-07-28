using System.Buffers.Binary;

namespace MiniCore.Compare.Interop.Audio;

public static class TestWaveFile
{
    public const int SampleRate = 8000;
    public const int WaveHeaderSize = 44;

    public static async Task CreateToneAsync(
        string path,
        TimeSpan duration,
        double frequencyHz = 697,
        CancellationToken ct = default)
    {
        var sampleCount = checked((int)(duration.TotalSeconds * SampleRate));
        var dataLength = checked(sampleCount * sizeof(short));
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

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate) * 12_000);
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(WaveHeaderSize + (i * sizeof(short)), sizeof(short)),
                sample);
        }

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    private static void WriteAscii(Span<byte> destination, int offset, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            destination[offset + i] = checked((byte)value[i]);
        }
    }
}
