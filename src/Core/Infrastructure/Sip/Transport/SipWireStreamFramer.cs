using System.Runtime.InteropServices;
using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Incrementally frames SIP messages from a TCP/TLS byte stream using
/// CRLFCRLF header boundaries plus Content-Length body sizing.
/// </summary>
internal sealed class SipWireStreamFramer
{
    /// <summary>
    /// Maximum bytes buffered for a single in-flight stream message (headers + body).
    /// A peer that never sends the CRLFCRLF header terminator, or that declares an
    /// oversized/overflowing Content-Length, would otherwise force unbounded buffering
    /// (memory-exhaustion DoS). Exceeding this limit aborts framing so the transport
    /// tears the connection down.
    /// </summary>
    private const int MaxMessageBytes = 262_144;

    private readonly List<byte> _buffer = [];
    private bool _consumedKeepalivePing;

    /// <summary>
    /// Appends newly received bytes to the internal framing buffer.
    /// </summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            _buffer.Add(bytes[i]);
    }

    /// <summary>
    /// True when the last <see cref="TryReadFrame"/> call consumed a double-CRLF
    /// keepalive ping (RFC 5626 §4.4.1) but produced no SIP message frame.
    /// Reset to false at the start of each <see cref="TryReadFrame"/> call.
    /// </summary>
    public bool ConsumedKeepalivePing => _consumedKeepalivePing;

    /// <summary>
    /// Resets the keepalive ping flag after the caller has handled it.
    /// </summary>
    public void ClearKeepalivePingFlag() => _consumedKeepalivePing = false;

    /// <summary>
    /// Tries to read one complete SIP message frame from the buffered stream bytes.
    /// </summary>
    public bool TryReadFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        TrimLeadingCrLf();
        if (_buffer.Count == 0)
            return false;

        var bufferSpan = CollectionsMarshal.AsSpan(_buffer);
        var headerEndIndex = IndexOfHeaderTerminator(bufferSpan);
        if (headerEndIndex < 0)
        {
            // Header terminator not seen yet. Keep waiting, but only up to the cap so a peer
            // that never terminates the headers cannot grow the buffer without bound.
            if (_buffer.Count > MaxMessageBytes)
                throw new InvalidOperationException(
                    $"SIP stream headers exceed {MaxMessageBytes} bytes without a terminator.");
            return false;
        }

        var headerLength = headerEndIndex + 4;
        var headerText = Encoding.UTF8.GetString(bufferSpan[..headerEndIndex]);
        if (HasChunkedTransferEncoding(headerText))
            throw new InvalidOperationException("SIP stream message MUST NOT use Transfer-Encoding: chunked.");

        if (!TryParseContentLength(headerText, out var hasContentLength, out var contentLength))
            throw new InvalidOperationException("SIP stream message has invalid Content-Length header.");
        if (!hasContentLength)
            throw new InvalidOperationException("SIP stream message over stream transport must include Content-Length.");

        // Compute in long to avoid int overflow from a near-int.MaxValue Content-Length, and
        // reject a declared size beyond the cap before it can drive unbounded buffering.
        var totalLength = (long)headerLength + contentLength;
        if (totalLength > MaxMessageBytes)
            throw new InvalidOperationException(
                $"SIP stream message length {totalLength} exceeds the {MaxMessageBytes}-byte maximum.");
        if (bufferSpan.Length < totalLength)
            return false;

        var frameLength = (int)totalLength;
        frame = GC.AllocateUninitializedArray<byte>(frameLength);
        bufferSpan[..frameLength].CopyTo(frame);
        _buffer.RemoveRange(0, frameLength);
        return true;
    }

    /// <summary>
    /// Finds first CRLFCRLF sequence in a buffered byte list.
    /// </summary>
    private static int IndexOfHeaderTerminator(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == (byte)'\r'
                && bytes[i + 1] == (byte)'\n'
                && bytes[i + 2] == (byte)'\r'
                && bytes[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Removes RFC3261-allowed leading CRLF preamble before a stream message start-line.
    /// Sets <see cref="ConsumedKeepalivePing"/> when a double-CRLF (RFC 5626 §4.4.1 ping) is trimmed.
    /// </summary>
    private void TrimLeadingCrLf()
    {
        if (_buffer.Count < 2)
            return;

        var bufferSpan = CollectionsMarshal.AsSpan(_buffer);
        var dropByteCount = 0;
        while (dropByteCount + 1 < bufferSpan.Length
               && bufferSpan[dropByteCount] == (byte)'\r'
               && bufferSpan[dropByteCount + 1] == (byte)'\n')
        {
            dropByteCount += 2;
        }

        if (dropByteCount >= 4)
            _consumedKeepalivePing = true;

        if (dropByteCount > 0)
            _buffer.RemoveRange(0, dropByteCount);
    }

    /// <summary>
    /// Parses Content-Length (or compact l) from SIP header text.
    /// Returns false when values are malformed or conflicting.
    /// </summary>
    private static bool TryParseContentLength(string headerText, out bool hasContentLength, out int contentLength)
    {
        hasContentLength = false;
        contentLength = 0;
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        int? parsed = null;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = rawLine[..separator].Trim();
            if (!key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("l", StringComparison.OrdinalIgnoreCase))
                continue;

            hasContentLength = true;
            var value = rawLine[(separator + 1)..].Trim();
            if (!int.TryParse(value, out var rowLength) || rowLength < 0)
                return false;
            if (parsed is null)
            {
                parsed = rowLength;
                continue;
            }

            if (parsed.Value != rowLength)
                return false;
        }

        if (parsed is null)
            return true;

        contentLength = parsed.Value;
        return true;
    }

    /// <summary>
    /// Returns true when Transfer-Encoding contains chunked token.
    /// </summary>
    private static bool HasChunkedTransferEncoding(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = rawLine[..separator].Trim();
            if (!key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = rawLine[(separator + 1)..].Trim();
            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
