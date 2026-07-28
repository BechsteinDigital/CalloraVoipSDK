using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

internal sealed class WavAudioFileReader : IAudioFileReader
{
    private readonly FileStream _stream;
    private readonly int _payloadType;
    private readonly int _sampleRate;
    private readonly int _frameBytes;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private long _bytesRead;
    private bool _disposed;

    public WavAudioFileReader(string filePath, AudioFileCodecContext context)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        _stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _payloadType = context.PayloadType;
        _frameBytes = Math.Max(context.SamplesPerFrame, 1) * 2;

        (_dataStart, _dataLength, _sampleRate) = ParseHeader(_stream, context.SampleRate);
        _stream.Seek(_dataStart, SeekOrigin.Begin);
    }

    public async ValueTask<AudioFileFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavAudioFileReader));

        var remaining = _dataLength - _bytesRead;
        if (remaining <= 0)
            return null;

        var bytesToRead = (int)Math.Min(_frameBytes, remaining);
        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await _stream
                .ReadAsync(buffer.AsMemory(totalRead, bytesToRead - totalRead), ct)
                .ConfigureAwait(false);

            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead <= 0)
            return null;

        _bytesRead += totalRead;

        if (totalRead != bytesToRead)
            Array.Resize(ref buffer, totalRead);

        var sampleCount = Math.Max(1, totalRead / 2);
        var durationRtpUnits = (uint)sampleCount;
        var delay = TimeSpan.FromSeconds(sampleCount / (double)Math.Max(1, _sampleRate));
        var frame = new MediaFrame(buffer, _payloadType, durationRtpUnits);
        return new AudioFileFrame(frame, delay);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the WAV RIFF/fmt/data header. Exposed to tests so the partial-read tolerance
    /// (Media #16) can be exercised with a short-read stream. Takes <see cref="Stream"/> because it
    /// only requires read/seek/position/length, all of which the base type provides.
    /// </summary>
    internal static (long DataStart, long DataLength, int SampleRate) ParseHeader(Stream stream, int fallbackSampleRate)
    {
        // Media #16: use ReadExactly so a header delivered in several short reads (e.g. from a
        // rate-limited stream) is assembled correctly instead of failing on the first partial read.
        Span<byte> riffHeader = stackalloc byte[12];
        if (!TryReadExactly(stream, riffHeader))
            throw new InvalidOperationException("Invalid WAV header: missing RIFF preamble.");

        if (riffHeader[0] != 'R' || riffHeader[1] != 'I' || riffHeader[2] != 'F' || riffHeader[3] != 'F')
            throw new InvalidOperationException("Invalid WAV header: RIFF signature missing.");

        if (riffHeader[8] != 'W' || riffHeader[9] != 'A' || riffHeader[10] != 'V' || riffHeader[11] != 'E')
            throw new InvalidOperationException("Invalid WAV header: WAVE signature missing.");

        var sampleRate = fallbackSampleRate > 0 ? fallbackSampleRate : 8000;
        long dataStart = -1;
        long dataLength = 0;
        var fmtFound = false;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            if (!TryReadExactly(stream, chunkHeader))
                break;

            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.Slice(4, 4));
            if (chunkSize < 0)
                throw new InvalidOperationException("Invalid WAV chunk size.");

            if (chunkHeader[0] == 'f' && chunkHeader[1] == 'm' && chunkHeader[2] == 't' && chunkHeader[3] == ' ')
            {
                var fmt = new byte[chunkSize];
                if (!TryReadExactly(stream, fmt))
                    throw new InvalidOperationException("Invalid WAV fmt chunk.");

                if (chunkSize < 16)
                    throw new InvalidOperationException("Unsupported WAV fmt chunk length.");

                var formatTag = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(0, 2));
                var channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                var bits = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(14, 2));

                if (formatTag != WavAudioFileCodec.PcmFormatTag
                    || channels != WavAudioFileCodec.ChannelsMono
                    || bits != WavAudioFileCodec.BitsPerSample16)
                {
                    throw new InvalidOperationException(
                        $"Unsupported WAV format: formatTag={formatTag}, channels={channels}, bits={bits}.");
                }

                fmtFound = true;
            }
            else if (chunkHeader[0] == 'd' && chunkHeader[1] == 'a' && chunkHeader[2] == 't' && chunkHeader[3] == 'a')
            {
                dataStart = stream.Position;
                dataLength = chunkSize;
                stream.Seek(chunkSize, SeekOrigin.Current);
            }
            else
            {
                stream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
                stream.Seek(1, SeekOrigin.Current);
        }

        if (!fmtFound)
            throw new InvalidOperationException("Invalid WAV file: fmt chunk missing.");

        if (dataStart < 0)
            throw new InvalidOperationException("Invalid WAV file: data chunk missing.");

        return (dataStart, dataLength, sampleRate > 0 ? sampleRate : 8000);
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes, looping across short reads. Returns
    /// <see langword="false"/> if the stream ends before the buffer is filled (Media #16).
    /// </summary>
    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                return false;

            total += read;
        }

        return true;
    }
}
