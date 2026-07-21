using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

/// <summary>
/// SIP wire parser/serializer for request and response messages.
/// </summary>
internal sealed class SipWireProtocol : ISipWireCodec
{
    /// <summary>
    /// Maximum accepted SIP message size in bytes (RFC 3261 §26.1.5 DoS guard).
    /// UDP SIP datagrams are capped at 65 535 bytes by IP; this limit applies uniformly
    /// across transports so that a single oversized message cannot cause unbounded allocation.
    /// </summary>
    private const int MaxMessageBytes = 65_536;

    /// <summary>
    /// Attempts to parse a SIP request from UTF-8 bytes.
    /// </summary>
    public bool TryParseRequest(
        ReadOnlySpan<byte> payload,
        out SipRequest? request)
    {
        request = null;
        if (!TrySplit(payload, out var firstLine, out var headers, out var body))
            return false;

        if (firstLine.StartsWith("SIP/2.0 ", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryParseRequestLine(firstLine, out var method, out var requestUri))
            return false;
        var filteredHeaders = FilterHeadersByMessageType(headers, isRequest: true);

        request = new SipRequest(
            method: method,
            requestUri: requestUri,
            headers: filteredHeaders,
            body: body);
        return true;
    }

    /// <summary>
    /// Attempts to parse a SIP response from UTF-8 bytes.
    /// </summary>
    public bool TryParseResponse(
        ReadOnlySpan<byte> payload,
        out SipResponse? response)
    {
        response = null;
        if (!TrySplit(payload, out var firstLine, out var headers, out var body))
            return false;

        if (!firstLine.StartsWith("SIP/2.0 ", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryParseStatusLine(firstLine, out var statusCode, out var reasonPhrase))
            return false;
        var filteredHeaders = FilterHeadersByMessageType(headers, isRequest: false);

        response = new SipResponse(
            statusCode: statusCode,
            reasonPhrase: reasonPhrase,
            headers: filteredHeaders,
            body: body);
        return true;
    }

    /// <summary>
    /// Serializes a SIP request into wire bytes.
    /// </summary>
    public byte[] SerializeRequest(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body = null)
    {
        if (!IsValidMethodToken(method))
            throw new ArgumentException("SIP request method must be a valid token without whitespace or separators.", nameof(method));
        if (!IsValidRequestUri(requestUri))
            throw new ArgumentException("SIP Request-URI is invalid for Request-Line serialization.", nameof(requestUri));
        ValidateSerializedBodyConstraints(headers, body);

        var bodyText = body ?? string.Empty;
        var map = EnsureContentLength(headers, bodyText);
        var builder = new StringBuilder();
        builder.Append(method.Trim()).Append(' ').Append(requestUri).Append(" SIP/2.0\r\n");
        AppendHeaderRows(builder, map);
        builder.Append("\r\n");
        if (bodyText.Length > 0) builder.Append(bodyText);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Serializes a SIP response into wire bytes.
    /// </summary>
    public byte[] SerializeResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body = null)
    {
        if (!IsValidStatusCode(statusCode))
            throw new ArgumentOutOfRangeException(nameof(statusCode), "SIP status code must be in range 100-699.");
        if (!IsValidReasonPhrase(reasonPhrase))
            throw new ArgumentException(
                "Reason phrase must not contain control characters (RFC 3261 §7.2).", nameof(reasonPhrase));
        ValidateSerializedBodyConstraints(headers, body);

        var bodyText = body ?? string.Empty;
        var map = EnsureContentLength(headers, bodyText);
        var builder = new StringBuilder();
        builder.Append("SIP/2.0 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n");
        AppendHeaderRows(builder, map);
        builder.Append("\r\n");
        if (bodyText.Length > 0) builder.Append(bodyText);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Splits SIP wire text into first line, headers and body.
    /// RFC 3261 §7.5: implementations MUST accept messages with bare LF line endings.
    /// </summary>
    private static bool TrySplit(
        ReadOnlySpan<byte> payload,
        out string firstLine,
        out IReadOnlyDictionary<string, string> headers,
        out string body)
    {
        firstLine = string.Empty;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = string.Empty;
        if (payload.Length == 0) return false;
        if (payload.Length > MaxMessageBytes) return false;

        if (!IndexOfHeaderTerminator(payload, out var headerTerminatorIndex, out var terminatorLength))
            return false;

        var headerLength = headerTerminatorIndex;
        // Normalize CRLF → LF so the header splitter handles both conventions uniformly.
        var headerText = Encoding.UTF8.GetString(payload[..headerLength])
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = headerText.Split('\n', StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            return false;

        firstLine = lines[0];
        var map = ParseHeaders(lines);
        if (!TryParseContentLength(map, out var hasContentLength, out var contentLength))
            return false;
        var bodyStart = headerTerminatorIndex + terminatorLength;
        var availableBodyBytes = payload.Length - bodyStart;
        if (availableBodyBytes < 0)
            return false;
        if (hasContentLength && availableBodyBytes < contentLength)
            return false;

        var bodyBytes = hasContentLength
            ? payload.Slice(bodyStart, contentLength)
            : payload[bodyStart..];
        if (!ValidateParsedBodyConstraints(map, bodyBytes.Length, hasContentLength, contentLength))
            return false;
        body = bodyBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bodyBytes);
        headers = map;
        return true;
    }

    /// <summary>
    /// Ensures Content-Length exists and matches the serialized body.
    /// </summary>
    private static IReadOnlyDictionary<string, string> EnsureContentLength(
        IReadOnlyDictionary<string, string> headers,
        string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var canonicalName = SipHeaderNames.Canonicalize(header.Key);
            if (string.IsNullOrWhiteSpace(canonicalName))
                continue;
            if (!IsValidMethodToken(canonicalName))
                continue;
            if (canonicalName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            var rows = SipHeaderValueStorage.SplitRows(header.Value);
            if (rows.Count == 0)
                continue;

            foreach (var row in rows)
            {
                ValidateSerializedHeaderValue(canonicalName, row);
                if (map.TryGetValue(canonicalName, out var existing))
                {
                    map[canonicalName] = SipHeaderRowRules.ShouldCombineRows(canonicalName, existing, row)
                        ? $"{existing}, {row}"
                        : SipHeaderValueStorage.AppendRow(existing, row);
                }
                else
                {
                    map[canonicalName] = row;
                }
            }
        }

        map["Content-Length"] = Encoding.UTF8.GetByteCount(body).ToString();
        return map;
    }

    /// <summary>
    /// Parses SIP headers including line folding and compact name expansion.
    /// </summary>
    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentHeaderName = null;
        StringBuilder? currentHeaderValue = null;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            if (char.IsWhiteSpace(line[0]))
            {
                if (currentHeaderValue is not null)
                    currentHeaderValue.Append(' ').Append(line.Trim());
                continue;
            }

            CommitHeader(map, ref currentHeaderName, ref currentHeaderValue);

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var rawName = line[..separatorIndex];
            var canonicalName = SipHeaderNames.Canonicalize(rawName);
            if (string.IsNullOrWhiteSpace(canonicalName))
                continue;
            if (!IsValidMethodToken(canonicalName))
                continue;

            currentHeaderName = canonicalName;
            currentHeaderValue = new StringBuilder(line[(separatorIndex + 1)..].Trim());
        }

        CommitHeader(map, ref currentHeaderName, ref currentHeaderValue);
        return map;
    }

    /// <summary>
    /// Commits one header/value pair into map, merging duplicates where applicable.
    /// </summary>
    private static void CommitHeader(
        IDictionary<string, string> map,
        ref string? headerName,
        ref StringBuilder? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName) || headerValue is null)
        {
            headerName = null;
            headerValue = null;
            return;
        }

        var value = headerValue.ToString().Trim();
        if (value.Length == 0)
        {
            headerName = null;
            headerValue = null;
            return;
        }

        if (map.TryGetValue(headerName, out var existing))
        {
            if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                map[headerName] = value;
            }
            else if (SipHeaderRowRules.ShouldCombineRows(headerName, existing, value))
            {
                map[headerName] = $"{existing}, {value}";
            }
            else
            {
                map[headerName] = SipHeaderValueStorage.AppendRow(existing, value);
            }
        }
        else
        {
            map[headerName] = value;
        }

        headerName = null;
        headerValue = null;
    }

    /// <summary>
    /// Parses Content-Length header value and validates duplicate-row consistency.
    /// </summary>
    private static bool TryParseContentLength(
        IReadOnlyDictionary<string, string> headers,
        out bool hasContentLength,
        out int contentLength)
    {
        hasContentLength = false;
        contentLength = 0;
        if (!headers.TryGetValue("Content-Length", out var contentLengthValue))
            return true;

        hasContentLength = true;
        var rows = SipHeaderValueStorage.SplitRows(contentLengthValue);
        if (rows.Count == 0)
            return false;

        int? parsedLength = null;
        foreach (var row in rows)
        {
            if (!int.TryParse(row, out var rowLength) || rowLength < 0)
                return false;
            if (parsedLength is null)
            {
                parsedLength = rowLength;
                continue;
            }

            if (parsedLength.Value != rowLength)
                return false;
        }

        if (parsedLength is null)
            return false;

        contentLength = parsedLength.Value;
        return true;
    }

    /// <summary>
    /// Finds the header/body terminator in raw payload.
    /// RFC 3261 §7.5: MUST accept bare LF line endings in addition to canonical CRLF.
    /// Returns true with the terminator start index and its byte length (4 for CRLFCRLF, 2 for LFLF).
    /// CRLFCRLF is preferred: a CRLFCRLF match takes priority over any earlier LFLF.
    /// </summary>
    private static bool IndexOfHeaderTerminator(
        ReadOnlySpan<byte> payload,
        out int index,
        out int terminatorLength)
    {
        index = -1;
        terminatorLength = 0;

        // First pass: look for canonical CRLFCRLF.
        for (var i = 0; i <= payload.Length - 4; i++)
        {
            if (payload[i]     == (byte)'\r'
                && payload[i + 1] == (byte)'\n'
                && payload[i + 2] == (byte)'\r'
                && payload[i + 3] == (byte)'\n')
            {
                index = i;
                terminatorLength = 4;
                return true;
            }
        }

        // Second pass: bare LFLF (RFC 3261 §7.5 MUST).
        for (var i = 0; i <= payload.Length - 2; i++)
        {
            if (payload[i] == (byte)'\n' && payload[i + 1] == (byte)'\n')
            {
                index = i;
                terminatorLength = 2;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses and validates a SIP request start line according to RFC3261 section 7.1.
    /// </summary>
    private static bool TryParseRequestLine(string firstLine, out string method, out string requestUri)
    {
        method = string.Empty;
        requestUri = string.Empty;

        if (string.IsNullOrWhiteSpace(firstLine))
            return false;
        if (firstLine.Contains('\t', StringComparison.Ordinal))
            return false;

        var firstSpaceIndex = firstLine.IndexOf(' ');
        if (firstSpaceIndex <= 0)
            return false;

        var secondSpaceIndex = firstLine.IndexOf(' ', firstSpaceIndex + 1);
        if (secondSpaceIndex <= firstSpaceIndex + 1)
            return false;

        if (firstLine.IndexOf(' ', secondSpaceIndex + 1) >= 0)
            return false;

        var parsedMethod = firstLine[..firstSpaceIndex];
        var parsedRequestUri = firstLine[(firstSpaceIndex + 1)..secondSpaceIndex];
        var parsedVersion = firstLine[(secondSpaceIndex + 1)..];

        if (!IsValidMethodToken(parsedMethod))
            return false;
        if (!IsValidRequestUri(parsedRequestUri))
            return false;
        if (!parsedVersion.Equals("SIP/2.0", StringComparison.OrdinalIgnoreCase))
            return false;

        method = parsedMethod.ToUpperInvariant();
        requestUri = parsedRequestUri;
        return true;
    }

    /// <summary>
    /// Returns true when the SIP method satisfies RFC token constraints.
    /// </summary>
    private static bool IsValidMethodToken(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return false;
        if (!method.Equals(method.Trim(), StringComparison.Ordinal))
            return false;

        foreach (var ch in method)
        {
            if (IsControl(ch) || IsSeparator(ch) || ch is ' ' or '\t')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when Request-URI is valid for a SIP Request-Line.
    /// </summary>
    private static bool IsValidRequestUri(string requestUri)
    {
        if (string.IsNullOrWhiteSpace(requestUri))
            return false;
        if (!requestUri.Equals(requestUri.Trim(), StringComparison.Ordinal))
            return false;
        if (requestUri.StartsWith("<", StringComparison.Ordinal) && requestUri.EndsWith(">", StringComparison.Ordinal))
            return false;

        var hasSchemeSeparator = false;
        foreach (var ch in requestUri)
        {
            if (ch == ':')
            {
                hasSchemeSeparator = true;
                break;
            }

            if (ch is '/' or '?' or '#')
                break;
        }

        if (!hasSchemeSeparator)
            return false;

        foreach (var ch in requestUri)
        {
            if (IsControl(ch) || char.IsWhiteSpace(ch))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parses and validates a SIP response status line according to RFC3261 section 7.2.
    /// </summary>
    private static bool TryParseStatusLine(string firstLine, out int statusCode, out string reasonPhrase)
    {
        statusCode = 0;
        reasonPhrase = string.Empty;

        if (string.IsNullOrEmpty(firstLine))
            return false;

        var firstSpaceIndex = firstLine.IndexOf(' ');
        if (firstSpaceIndex <= 0)
            return false;

        var secondSpaceIndex = firstLine.IndexOf(' ', firstSpaceIndex + 1);
        if (secondSpaceIndex <= firstSpaceIndex + 1)
            return false;

        if (firstLine[..firstSpaceIndex].Contains('\t', StringComparison.Ordinal))
            return false;

        var sipVersion = firstLine[..firstSpaceIndex];
        if (!sipVersion.Equals("SIP/2.0", StringComparison.OrdinalIgnoreCase))
            return false;

        var statusCodeToken = firstLine[(firstSpaceIndex + 1)..secondSpaceIndex];
        if (!TryParseStatusCode(statusCodeToken, out statusCode))
            return false;

        reasonPhrase = firstLine[(secondSpaceIndex + 1)..];
        if (!IsValidReasonPhrase(reasonPhrase))
            return false;

        return true;
    }

    /// <summary>
    /// Parses numeric SIP status code token and validates RFC range/class.
    /// </summary>
    private static bool TryParseStatusCode(string token, out int statusCode)
    {
        statusCode = 0;
        if (token.Length != 3)
            return false;
        foreach (var ch in token)
        {
            if (!char.IsDigit(ch))
                return false;
        }
        if (!int.TryParse(token, out statusCode))
            return false;
        return IsValidStatusCode(statusCode);
    }

    /// <summary>
    /// Returns true when SIP status code is within RFC3261 valid class ranges.
    /// </summary>
    private static bool IsValidStatusCode(int statusCode) => statusCode is >= 100 and <= 699;

    /// <summary>
    /// Returns true when a status reason phrase does not break Status-Line framing.
    /// </summary>
    private static bool IsValidReasonPhrase(string reasonPhrase)
    {
        foreach (var ch in reasonPhrase)
        {
            // RFC 3261 §7.2: the Reason-Phrase permits SP/HTAB and printable/UTF-8 text but no other control
            // characters. Reject every C0 control (except HTAB) and DEL — not just CR/LF — so an embedded NUL or
            // other control cannot corrupt or inject into the status line (CF-067).
            if ((ch < ' ' && ch != '\t') || ch == '\x7f')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Removes message-category-inapplicable headers per RFC3261 section 7.3.2.
    /// </summary>
    private static IReadOnlyDictionary<string, string> FilterHeadersByMessageType(
        IReadOnlyDictionary<string, string> headers,
        bool isRequest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (isRequest)
            {
                if (!SipHeaderRowRules.IsApplicableToRequest(header.Key))
                    continue;
            }
            else
            {
                if (!SipHeaderRowRules.IsApplicableToResponse(header.Key))
                    continue;
            }

            map[header.Key] = header.Value;
        }

        return map;
    }

    /// <summary>
    /// Appends header rows while preserving non-combined row boundaries.
    /// </summary>
    private static void AppendHeaderRows(
        StringBuilder builder,
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            var rows = SipHeaderValueStorage.SplitRows(header.Value);
            if (rows.Count == 0)
            {
                ValidateSerializedHeaderValue(header.Key, string.Empty);
                builder.Append(header.Key).Append(": ").Append("\r\n");
                continue;
            }

            foreach (var row in rows)
            {
                ValidateSerializedHeaderValue(header.Key, row);
                builder.Append(header.Key).Append(": ").Append(row).Append("\r\n");
            }
        }
    }

    /// <summary>
    /// Validates serialized header row value to avoid line-break injection.
    /// </summary>
    private static void ValidateSerializedHeaderValue(string headerName, string value)
    {
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n')
                throw new ArgumentException($"Header value for '{headerName}' contains CR/LF.", nameof(value));
        }
    }

    /// <summary>
    /// Validates body-related header requirements for parsed messages (RFC3261 7.4/7.4.1/7.4.2).
    /// </summary>
    private static bool ValidateParsedBodyConstraints(
        IReadOnlyDictionary<string, string> headers,
        int parsedBodyLength,
        bool hasContentLength,
        int contentLength)
    {
        if (hasContentLength && parsedBodyLength == 0 && contentLength != 0)
            return false;
        if (parsedBodyLength > 0)
        {
            if (!headers.TryGetValue("Content-Type", out var contentType)
                || string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }
        }

        if (HasChunkedTransferEncoding(headers))
            return false;

        if (parsedBodyLength == 0
            && headers.TryGetValue("Content-Encoding", out var contentEncoding)
            && !string.IsNullOrWhiteSpace(contentEncoding))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates body-related header requirements for serialized messages (RFC3261 7.4/7.4.1/7.4.2).
    /// </summary>
    private static void ValidateSerializedBodyConstraints(
        IReadOnlyDictionary<string, string> headers,
        string? body)
    {
        var bodyText = body ?? string.Empty;
        var hasBody = bodyText.Length > 0;

        var contentType = TryGetHeaderValue(headers, "Content-Type");
        if (hasBody && string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException(
                "Content-Type header is required when serializing a non-empty SIP message body.",
                nameof(headers));
        }

        var contentEncoding = TryGetHeaderValue(headers, "Content-Encoding");
        if (!string.IsNullOrWhiteSpace(contentEncoding) && !hasBody)
        {
            throw new ArgumentException(
                "Content-Encoding header must be omitted when SIP message body is empty.",
                nameof(headers));
        }

        if (HasChunkedTransferEncoding(headers))
        {
            throw new ArgumentException(
                "Transfer-Encoding: chunked MUST NOT be used in SIP messages.",
                nameof(headers));
        }
    }

    /// <summary>
    /// Gets one canonical header value from an arbitrary input map.
    /// </summary>
    private static string? TryGetHeaderValue(
        IReadOnlyDictionary<string, string> headers,
        string canonicalName)
    {
        if (headers.TryGetValue(canonicalName, out var value))
            return value;

        foreach (var header in headers)
        {
            var candidate = SipHeaderNames.Canonicalize(header.Key);
            if (candidate.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }

    /// <summary>
    /// Returns true when one parsed header map contains Transfer-Encoding chunked.
    /// </summary>
    private static bool HasChunkedTransferEncoding(IReadOnlyDictionary<string, string> headers)
    {
        var value = TryGetHeaderValue(headers, "Transfer-Encoding");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true for ASCII control characters, including DEL.
    /// </summary>
    private static bool IsControl(char value) => value is <= '\u001F' or '\u007F';

    /// <summary>
    /// Returns true for HTTP/SIP token separator characters.
    /// </summary>
    private static bool IsSeparator(char value) =>
        value is '(' or ')' or '<' or '>' or '@'
            or ',' or ';' or ':' or '\\' or '"'
            or '/' or '[' or ']' or '?' or '='
            or '{' or '}';
}
