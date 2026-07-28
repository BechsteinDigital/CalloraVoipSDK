using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Media;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

internal sealed class Mp3PassthroughReader : IAudioFileReader
{
    private readonly FileStream _stream;
    private readonly int _payloadType;
    private readonly int _clockRate;
    private bool _headerRegionScanned;
    private bool _disposed;

    public Mp3PassthroughReader(string filePath, AudioFileCodecContext context)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        _payloadType = context.PayloadType;
        _clockRate = context.ClockRate > 0 ? context.ClockRate : 90000;

        _stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async ValueTask<AudioFileFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Mp3PassthroughReader));

        // Media #16: before the first frame, skip a leading ID3v2 tag and resynchronise to the
        // next MP3 sync word instead of hard-failing. Real-world MP3s frequently start with an
        // ID3v2 header and/or a few stray bytes before the first frame.
        if (!_headerRegionScanned)
        {
            await SkipLeadingTagsAndAlignAsync(ct).ConfigureAwait(false);
            _headerRegionScanned = true;
        }

        var headerBytes = new byte[4];
        var headerRead = await ReadExactAsync(headerBytes, ct).ConfigureAwait(false);
        if (headerRead == 0)
            return null;

        if (headerRead < headerBytes.Length)
            throw new InvalidOperationException("Unexpected end-of-file while reading MP3 frame header.");

        if (!Mp3FrameParser.TryReadHeader(headerBytes, out var header))
            throw new InvalidOperationException("Invalid MP3 frame header.");

        var frameLength = header.FrameLengthBytes;
        if (frameLength < 4)
            throw new InvalidOperationException("Invalid MP3 frame length.");

        var payload = new byte[frameLength];
        Buffer.BlockCopy(headerBytes, 0, payload, 0, 4);

        var remaining = frameLength - 4;
        if (remaining > 0)
        {
            var bodyRead = await ReadExactAsync(payload.AsMemory(4, remaining), ct).ConfigureAwait(false);
            if (bodyRead < remaining)
                throw new InvalidOperationException("Unexpected end-of-file while reading MP3 frame payload.");
        }

        var delay = TimeSpan.FromSeconds(header.SamplesPerFrame / (double)header.SampleRateHz);
        var durationRtpUnits = (uint)Math.Max(
            1,
            (int)Math.Round(header.SamplesPerFrame * (_clockRate / (double)header.SampleRateHz)));

        return new AudioFileFrame(new MediaFrame(payload, _payloadType, durationRtpUnits), delay);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Skips a leading ID3v2 tag (if present) and positions the stream at the first byte of the
    /// next valid MP3 frame, discarding any junk bytes before it. RFC-agnostic; ID3v2 layout per
    /// the ID3v2.3/2.4 informal spec (10-byte header, synchsafe size).
    /// </summary>
    private async ValueTask SkipLeadingTagsAndAlignAsync(CancellationToken ct)
    {
        await SkipId3v2TagAsync(ct).ConfigureAwait(false);
        await AlignToNextFrameAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask SkipId3v2TagAsync(CancellationToken ct)
    {
        // ID3v2 header: "ID3" + version (2) + flags (1) + size (4, synchsafe: 7 bits per byte).
        var head = new byte[10];
        var read = await ReadExactAsync(head, ct).ConfigureAwait(false);
        if (read < head.Length
            || head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'3'
            || head[3] == 0xFF || head[4] == 0xFF)
        {
            // Not an ID3v2 tag: rewind so the aligner sees the original bytes.
            _stream.Seek(-read, SeekOrigin.Current);
            return;
        }

        var tagSize = ((head[6] & 0x7F) << 21)
            | ((head[7] & 0x7F) << 14)
            | ((head[8] & 0x7F) << 7)
            | (head[9] & 0x7F);

        // A footer (10 bytes) is present when the footer-present flag (bit 4 of flags) is set.
        var footerBytes = (head[5] & 0x10) != 0 ? 10 : 0;
        _stream.Seek(tagSize + footerBytes, SeekOrigin.Current);
    }

    private async ValueTask AlignToNextFrameAsync(CancellationToken ct)
    {
        // Byte-scan forward until a valid 4-byte MP3 frame header is found, then position the
        // stream exactly at that sync word so the normal frame reader consumes it as-is.
        var window = new byte[4];
        var filled = 0;

        while (true)
        {
            if (filled < window.Length)
            {
                var read = await _stream
                    .ReadAsync(window.AsMemory(filled, window.Length - filled), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    // No sync word found before EOF; leave the stream at EOF so the frame
                    // reader returns null (empty/tag-only input) rather than crashing.
                    return;
                }

                filled += read;
                continue;
            }

            if (Mp3FrameParser.TryReadHeader(window, out _))
            {
                // Rewind to the start of this frame header.
                _stream.Seek(-window.Length, SeekOrigin.Current);
                return;
            }

            // Slide the window forward by one byte and pull in one more.
            window[0] = window[1];
            window[1] = window[2];
            window[2] = window[3];
            filled = 3;
        }
    }

    private async ValueTask<int> ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private async ValueTask<int> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return total;
    }
}
