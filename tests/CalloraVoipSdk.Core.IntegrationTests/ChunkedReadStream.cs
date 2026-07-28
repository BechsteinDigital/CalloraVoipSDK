namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A seekable in-memory stream that caps how many bytes each <see cref="Read(byte[], int, int)"/>
/// returns, reproducing the short-read behaviour of rate-limited or network streams. Used to
/// exercise partial-read tolerance in header parsers (issue #16, Media 5).
/// </summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly int _maxBytesPerRead;

    public ChunkedReadStream(byte[] data, int maxBytesPerRead)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (maxBytesPerRead < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerRead));

        _inner = new MemoryStream(data, writable: false);
        _maxBytesPerRead = maxBytesPerRead;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));

    public override int Read(Span<byte> buffer)
        => _inner.Read(buffer[..Math.Min(buffer.Length, _maxBytesPerRead)]);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
