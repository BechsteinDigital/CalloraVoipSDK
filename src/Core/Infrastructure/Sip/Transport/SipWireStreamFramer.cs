using System.Runtime.InteropServices;
using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Incrementally frames SIP messages from a TCP/TLS byte stream using
/// CRLFCRLF header boundaries plus Content-Length body sizing.
/// </summary>
internal sealed class SipWireStreamFramer
{
    private readonly List<byte> _buffer = [];
    private bool _consumedKeepalivePing;

    /// <summary>
    /// Appends newly received bytes to the internal framing buffer.
    /// </summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes);
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
            return false;

        var headerLength = headerEndIndex + 4;
        var headerText = Encoding.UTF8.GetString(bufferSpan[..headerEndIndex]);
        // Split the header into lines once and reuse for both header checks — the two
        // parsers previously each re-split the same header text, doubling the allocation.
        var headerLines = headerText.Split("\r\n", StringSplitOptions.None);
        if (HasChunkedTransferEncoding(headerLines))
            throw new InvalidOperationException("SIP stream message MUST NOT use Transfer-Encoding: chunked.");

        if (!TryParseContentLength(headerLines, out var hasContentLength, out var contentLength))
            throw new InvalidOperationException("SIP stream message has invalid Content-Length header.");
        if (!hasContentLength)
            throw new InvalidOperationException("SIP stream message over stream transport must include Content-Length.");

        var totalLength = headerLength + contentLength;
        if (bufferSpan.Length < totalLength)
            return false;

        frame = GC.AllocateUninitializedArray<byte>(totalLength);
        bufferSpan[..totalLength].CopyTo(frame);
        _buffer.RemoveRange(0, totalLength);
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
    private static bool TryParseContentLength(string[] lines, out bool hasContentLength, out int contentLength)
    {
        hasContentLength = false;
        contentLength = 0;
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
    private static bool HasChunkedTransferEncoding(string[] lines)
    {
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
