namespace CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

/// <summary>
/// SIP protocol helpers for IDs, header formatting, and token extraction.
/// </summary>
internal static class SipProtocol
{
    private static readonly string LocalBranchPrefix = CreateLocalBranchPrefix();

    /// <summary>
    /// Generates a SIP branch token for Via headers.
    /// </summary>
    public static string NewBranch() => $"{LocalBranchPrefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Generates a SIP tag token for From/To headers.
    /// </summary>
    public static string NewTag() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// Generates a SIP Call-ID.
    /// </summary>
    public static string NewCallId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Returns true when a SIP status code is provisional (1xx).
    /// </summary>
    public static bool IsProvisional(int statusCode) => statusCode is >= 100 and < 200;

    /// <summary>
    /// Returns true when a SIP status code is success (2xx).
    /// </summary>
    public static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// Returns true when a URI (or name-addr containing a URI) uses the SIPS scheme.
    /// </summary>
    public static bool IsSipsUri(string? uriOrNameAddr)
    {
        if (string.IsNullOrWhiteSpace(uriOrNameAddr))
            return false;

        var parsed = ExtractUriFromNameAddr(uriOrNameAddr);
        return parsed.StartsWith("sips:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes one UAC-facing response status code according to RFC3261 section 8.1.3.2.
    /// </summary>
    public static int NormalizeUacResponseStatusCode(int statusCode)
    {
        if (statusCode is < 100 or > 699)
            return statusCode;

        if (statusCode < 200)
        {
            return statusCode switch
            {
                100 or 180 or 181 or 182 or 183 => statusCode,
                _ => 183
            };
        }

        return statusCode switch
        {
            200 => 200,
            300 or 301 or 302 or 305 or 380 => statusCode,
            400 or 401 or 402 or 403 or 404 or 405 or 406 or 407 or 408 or 410
                or 413 or 414 or 415 or 416 or 420 or 421 or 423
                or 480 or 481 or 482 or 483 or 484 or 485 or 486
                or 487 or 488 or 491 or 493 => statusCode,
            500 or 501 or 502 or 503 or 504 or 505 or 513 => statusCode,
            600 or 603 or 604 or 606 => statusCode,
            _ => statusCode / 100 * 100
        };
    }

    /// <summary>
    /// Counts Via header field values contained in one Via header row payload.
    /// </summary>
    public static int CountViaHeaderValues(string? viaHeader)
    {
        if (string.IsNullOrWhiteSpace(viaHeader))
            return 0;

        return viaHeader
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    /// <summary>
    /// Formats a SIP name-addr header with optional display name and tag.
    /// </summary>
    public static string FormatNameAddr(string? displayName, string uri, string? tag = null)
    {
        var escapedDisplay = string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : $"\"{displayName.Trim().Replace("\"", "\\\"")}\" ";
        var value = $"{escapedDisplay}<{uri}>";
        return string.IsNullOrWhiteSpace(tag) ? value : $"{value};tag={tag}";
    }

    /// <summary>
    /// Extracts URI text from a SIP name-addr header.
    /// </summary>
    public static string ExtractUriFromNameAddr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        var left = trimmed.IndexOf('<');
        var right = trimmed.IndexOf('>');
        if (left >= 0 && right > left) return trimmed[(left + 1)..right].Trim();
        var semicolon = trimmed.IndexOf(';');
        return semicolon > 0 ? trimmed[..semicolon].Trim() : trimmed;
    }

    /// <summary>
    /// Extracts the tag parameter value from a SIP header.
    /// </summary>
    public static string? ExtractTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var markerIndex = value.IndexOf(";tag=", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        var tail = value[(markerIndex + 5)..];
        var endIndex = tail.IndexOf(';');
        return endIndex >= 0 ? tail[..endIndex].Trim() : tail.Trim();
    }

    /// <summary>
    /// Extracts the SIP method token from a CSeq header.
    /// </summary>
    public static string? ExtractCSeqMethod(string? cseq)
    {
        if (string.IsNullOrWhiteSpace(cseq)) return null;
        var parts = cseq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1].Trim().ToUpperInvariant() : null;
    }

    /// <summary>
    /// Extracts the numeric sequence number from a CSeq header.
    /// </summary>
    public static int ExtractCSeqNumber(string? cseq)
    {
        if (string.IsNullOrWhiteSpace(cseq)) return 0;
        var parts = cseq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 1 && int.TryParse(parts[0], out var number) ? number : 0;
    }

    /// <summary>
    /// Extracts first Via entry from a possibly comma-separated Via header.
    /// </summary>
    public static string? ExtractTopViaEntry(string? viaHeader)
    {
        if (string.IsNullOrWhiteSpace(viaHeader))
            return null;

        var topVia = viaHeader.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrWhiteSpace(topVia) ? null : topVia.Trim();
    }

    /// <summary>
    /// Extracts Via branch value from the top Via header.
    /// Supports comma-separated Via chains by reading only the first entry.
    /// </summary>
    public static string? ExtractViaBranch(string? viaHeader)
    {
        var topVia = ExtractTopViaEntry(viaHeader);
        if (string.IsNullOrWhiteSpace(topVia))
            return null;
        var markerIndex = topVia.IndexOf("branch=", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var tail = topVia[(markerIndex + "branch=".Length)..];
        var endIndex = tail.IndexOfAny([';', ' ', '\t', ',']);
        var branch = endIndex >= 0 ? tail[..endIndex] : tail;
        return string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
    }

    /// <summary>
    /// Extracts top Via sent-by value (`host[:port]`) used in RFC3261 transaction matching.
    /// Supports comma-separated Via chains by reading only the first entry.
    /// </summary>
    public static string? ExtractViaSentBy(string? viaHeader)
    {
        var topVia = ExtractTopViaEntry(viaHeader);
        if (string.IsNullOrWhiteSpace(topVia))
            return null;
        var firstWhitespace = topVia.IndexOfAny([' ', '\t']);
        if (firstWhitespace < 0 || firstWhitespace >= topVia.Length - 1)
            return null;

        var sentByAndParameters = topVia[(firstWhitespace + 1)..].TrimStart();
        if (string.IsNullOrWhiteSpace(sentByAndParameters))
            return null;

        var endIndex = sentByAndParameters.IndexOfAny([';', ' ', '\t', ',']);
        var sentBy = endIndex >= 0 ? sentByAndParameters[..endIndex] : sentByAndParameters;
        return string.IsNullOrWhiteSpace(sentBy) ? null : sentBy.Trim();
    }

    /// <summary>
    /// Reads the public address a registrar reflected back in the top Via of its response
    /// via the RFC 3261 §18.2.1 <c>received=</c> and RFC 3581 §4 <c>rport=</c> parameters.
    /// This is the caller's public IP/port as the server actually saw it — the reliable
    /// source for a NAT-routable Contact. Returns nulls when a parameter is absent.
    /// </summary>
    public static (string? Host, int? Port) ExtractViaReceivedRport(string? viaHeader)
    {
        var topVia = ExtractTopViaEntry(viaHeader);
        if (string.IsNullOrWhiteSpace(topVia))
            return (null, null);

        return (ReadViaParameter(topVia, "received"), ParsePort(ReadViaParameter(topVia, "rport")));

        static string? ReadViaParameter(string via, string name)
        {
            var marker = ";" + name + "=";
            var index = via.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            var tail = via[(index + marker.Length)..];
            var end = tail.IndexOfAny([';', ' ', '\t', ',']);
            var value = end >= 0 ? tail[..end] : tail;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static int? ParsePort(string? value) =>
            int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port : null;
    }

    /// <summary>
    /// Reflects Via parameters per RFC 3261 §18.2.1 and RFC 3581 §4:
    /// <list type="bullet">
    ///   <item>If source IP differs from Via sent-by host, adds <c>;received=&lt;ip&gt;</c> (RFC 3261 §18.2.1 MUST).</item>
    ///   <item>If the top Via has a bare <c>;rport</c> (no value), fills in <c>rport=&lt;port&gt;</c> (RFC 3581 §4 MUST).</item>
    /// </list>
    /// Returns the original header unchanged when neither condition applies.
    /// </summary>
    public static string ReflectViaParameters(string? viaHeader, System.Net.IPEndPoint actualRemote)
    {
        if (string.IsNullOrWhiteSpace(viaHeader) || actualRemote is null)
            return viaHeader ?? string.Empty;

        var topVia = ExtractTopViaEntry(viaHeader);
        if (string.IsNullOrWhiteSpace(topVia))
            return viaHeader;

        var actualIp = actualRemote.Address.ToString();
        var actualPort = actualRemote.Port;

        // RFC 3261 §18.2.1: determine whether source IP differs from Via sent-by host.
        var sentBy = ExtractViaSentBy(viaHeader) ?? string.Empty;
        var sentByHost = sentBy.Contains(':') ? sentBy[..sentBy.LastIndexOf(':')] : sentBy;
        if (sentByHost.StartsWith("[", StringComparison.Ordinal) && sentByHost.EndsWith("]", StringComparison.Ordinal))
            sentByHost = sentByHost[1..^1];
        var ipDiffers = !string.Equals(sentByHost, actualIp, StringComparison.OrdinalIgnoreCase);

        // RFC 3581 §4: detect bare ;rport (present without '=').
        var rportIndex = topVia.IndexOf(";rport", StringComparison.OrdinalIgnoreCase);
        var hasBareRport = false;
        if (rportIndex >= 0)
        {
            var afterRport = rportIndex + ";rport".Length;
            hasBareRport = afterRport >= topVia.Length || topVia[afterRport] != '=';
        }

        if (!hasBareRport && !ipDiffers)
            return viaHeader; // nothing to reflect

        string updatedTopVia;
        if (hasBareRport)
        {
            // Fill rport=<port> and optionally received=<ip>
            var afterRport = rportIndex + ";rport".Length;
            updatedTopVia = topVia[..afterRport] + "=" + actualPort.ToString()
                + (ipDiffers ? ";received=" + actualIp : string.Empty)
                + topVia[afterRport..];
        }
        else
        {
            // RFC 3261 §18.2.1: insert ;received= after sent-by, before parameters
            var insertAt = FindSentByEnd(topVia);
            updatedTopVia = topVia[..insertAt] + ";received=" + actualIp + topVia[insertAt..];
        }

        var parts = viaHeader.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 1 ? updatedTopVia : updatedTopVia + ", " + parts[1];
    }

    /// <summary>
    /// Legacy alias for <see cref="ReflectViaParameters"/>.
    /// </summary>
    public static string ReflectViaRport(string? viaHeader, System.Net.IPEndPoint actualRemote) =>
        ReflectViaParameters(viaHeader, actualRemote);

    /// <summary>
    /// Resolves the UDP response destination per RFC 3261 §18.2.2.
    /// If the Via contains a <c>received</c> parameter the response goes to that IP;
    /// the port is taken from <c>rport</c> (if present with a value), then from the
    /// sent-by port, then defaults to 5060.
    /// Falls back to <paramref name="actualRemote"/> when the Via cannot be parsed.
    /// </summary>
    public static System.Net.IPEndPoint ResolveUdpResponseDestination(
        string? viaHeader,
        System.Net.IPEndPoint actualRemote)
    {
        var topVia = ExtractTopViaEntry(viaHeader);
        if (string.IsNullOrWhiteSpace(topVia))
            return actualRemote;

        // Extract ;received=<ip>
        string? receivedIpStr = null;
        var receivedIdx = topVia.IndexOf("received=", StringComparison.OrdinalIgnoreCase);
        if (receivedIdx >= 0 && (receivedIdx == 0 || topVia[receivedIdx - 1] == ';'))
        {
            var start = receivedIdx + "received=".Length;
            var end = start;
            while (end < topVia.Length && topVia[end] != ';' && topVia[end] != ',')
                end++;
            receivedIpStr = topVia[start..end].Trim();
        }

        // Extract rport=<port> (with value, not bare)
        int? rportValue = null;
        var rportIdx = topVia.IndexOf("rport=", StringComparison.OrdinalIgnoreCase);
        if (rportIdx >= 0 && (rportIdx == 0 || topVia[rportIdx - 1] == ';'))
        {
            var start = rportIdx + "rport=".Length;
            var end = start;
            while (end < topVia.Length && char.IsDigit(topVia[end]))
                end++;
            if (end > start && int.TryParse(topVia[start..end], out var p))
                rportValue = p;
        }

        // Determine target IP: received > actual source
        System.Net.IPAddress targetIp;
        if (!string.IsNullOrEmpty(receivedIpStr)
            && System.Net.IPAddress.TryParse(receivedIpStr, out var parsed))
            targetIp = parsed;
        else
            targetIp = actualRemote.Address;

        // Determine target port: rport > sent-by port > 5060
        int targetPort;
        if (rportValue.HasValue)
        {
            targetPort = rportValue.Value;
        }
        else
        {
            var sentBy = ExtractViaSentBy(viaHeader) ?? string.Empty;
            var colonIdx = sentBy.LastIndexOf(':');
            targetPort = colonIdx >= 0 && int.TryParse(sentBy[(colonIdx + 1)..], out var sbPort)
                ? sbPort
                : 5060;
        }

        return new System.Net.IPEndPoint(targetIp, targetPort);
    }

    /// <summary>
    /// Returns the index in <paramref name="topVia"/> where sent-by ends (first ';' or end-of-string).
    /// </summary>
    private static int FindSentByEnd(string topVia)
    {
        var firstSpace = topVia.IndexOfAny([' ', '\t']);
        if (firstSpace < 0)
            return topVia.Length;
        var afterVersion = firstSpace + 1;
        while (afterVersion < topVia.Length
               && (topVia[afterVersion] == ' ' || topVia[afterVersion] == '\t'))
            afterVersion++;
        var semi = topVia.IndexOf(';', afterVersion);
        return semi >= 0 ? semi : topVia.Length;
    }

    /// <summary>
    /// Returns true when Via branch contains RFC3261 magic cookie.
    /// </summary>
    public static bool HasMagicCookie(string? branch) =>
        !string.IsNullOrWhiteSpace(branch)
        && branch.StartsWith("z9hG4bK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when a Via branch value was generated by this SDK instance.
    /// </summary>
    public static bool IsLocalBranch(string? branch) =>
        !string.IsNullOrWhiteSpace(branch)
        && branch.StartsWith(LocalBranchPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds this runtime's local branch prefix used for loop detection.
    /// </summary>
    private static string CreateLocalBranchPrefix()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        return $"z9hG4bK-vsdk-{token}-";
    }

    /// <summary>
    /// Parses SIP URI user (optional), host and optional port.
    /// </summary>
    public static bool TryParseSipUri(
        string uri,
        out string user,
        out string host,
        out int? port)
    {
        user = string.Empty;
        host = string.Empty;
        port = null;
        if (string.IsNullOrWhiteSpace(uri)) return false;

        var value = uri.Trim();
        if (value.StartsWith("<", StringComparison.Ordinal) && value.EndsWith(">", StringComparison.Ordinal))
            value = value[1..^1];
        var hadSipScheme = false;
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
            hadSipScheme = true;
        }
        if (value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[5..];
            hadSipScheme = true;
        }
        if (!hadSipScheme)
            return false;

        var atIndex = value.IndexOf('@');
        var hostPart = value;
        if (atIndex >= 0)
        {
            if (atIndex == 0 || atIndex == value.Length - 1)
                return false;

            user = value[..atIndex];
            hostPart = value[(atIndex + 1)..];
        }

        var headerIndex = hostPart.IndexOf('?');
        if (headerIndex >= 0)
            hostPart = hostPart[..headerIndex];

        var parameterIndex = hostPart.IndexOf(';');
        if (parameterIndex >= 0)
            hostPart = hostPart[..parameterIndex];

        hostPart = hostPart.Trim();
        if (hostPart.Length == 0)
            return false;

        if (hostPart.Length > 0 && hostPart[0] == '[')
        {
            var bracketEnd = hostPart.IndexOf(']');
            if (bracketEnd <= 1)
                return false;

            host = hostPart[1..bracketEnd];
            if (hostPart.Length > bracketEnd + 1)
            {
                if (hostPart[bracketEnd + 1] != ':')
                    return false;

                var portText = hostPart[(bracketEnd + 2)..];
                if (!int.TryParse(portText, out var parsedPort))
                    return false;

                port = parsedPort;
            }

            return !string.IsNullOrWhiteSpace(host);
        }

        var colonIndex = hostPart.LastIndexOf(':');
        if (colonIndex > 0
            && colonIndex < hostPart.Length - 1
            && int.TryParse(hostPart[(colonIndex + 1)..], out var parsedPortWithHost))
        {
            host = hostPart[..colonIndex];
            port = parsedPortWithHost;
        }
        else
        {
            host = hostPart;
        }

        return !string.IsNullOrWhiteSpace(host);
    }

    // -----------------------------------------------------------------------
    // §19.1.2 — Character Escaping
    // -----------------------------------------------------------------------

    // Characters that do NOT require percent-encoding in the SIP URI user part.
    // unreserved: ALPHA / DIGIT / "-" / "_" / "." / "!" / "~" / "*" / "'" / "(" / ")"
    // user-unreserved: "&" / "=" / "+" / "$" / "," / ";" / "?" / "/"
    private static readonly System.Collections.Generic.HashSet<char> SipUserUnreserved =
    [
        '-', '_', '.', '!', '~', '*', '\'', '(', ')',
        '&', '=', '+', '$', ',', ';', '?', '/'
    ];

    /// <summary>
    /// Percent-encodes characters in the user-info portion of a SIP URI that fall outside
    /// the RFC 3261 §19.1.2 unreserved + user-unreserved set.
    /// Digits and ASCII letters are always left unencoded.
    /// </summary>
    public static string SipUriEncodeUser(string? user)
    {
        if (string.IsNullOrEmpty(user))
            return string.Empty;

        var sb = new System.Text.StringBuilder(user.Length);
        foreach (var ch in user)
        {
            if (char.IsAsciiLetterOrDigit(ch) || SipUserUnreserved.Contains(ch))
                sb.Append(ch);
            else
            {
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(ch.ToString()))
                    sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decodes percent-encoded sequences in a SIP URI user-info portion (RFC 3261 §19.1.2).
    /// </summary>
    public static string SipUriDecodeUser(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return string.Empty;

        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return encoded;
        }
    }

    // -----------------------------------------------------------------------
    // §19.1.4 — URI Comparison
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compares two SIP or SIPS URIs per RFC 3261 §19.1.4.
    /// Rules applied:
    /// <list type="bullet">
    ///   <item>Scheme: case-insensitive; sip ≠ sips.</item>
    ///   <item>User: case-sensitive (for user=phone: visual-separator-normalized then case-insensitive).</item>
    ///   <item>Host: case-insensitive.</item>
    ///   <item>Port: resolved to scheme default (5060/5061) when absent.</item>
    ///   <item><c>transport</c> parameter: default is "udp"; absent ≡ transport=udp.</item>
    ///   <item>Other known parameters (maddr, ttl, user, method, lr): absent in one URI but
    ///         present in the other makes the URIs not equal.</item>
    ///   <item>Unknown URI parameters: presence on one side but not the other makes the URIs not equal.</item>
    ///   <item>URI headers: all headers present in either URI must be equal in both.</item>
    /// </list>
    /// Name-addr wrappers (&lt;...&gt;) and display names are stripped before comparison.
    /// </summary>
    public static bool SipUriEqual(string? uriA, string? uriB)
    {
        if (ReferenceEquals(uriA, uriB)) return true;
        if (uriA is null || uriB is null) return false;

        if (!TryDecomposeSipUri(uriA, out var a) || !TryDecomposeSipUri(uriB, out var b))
            return string.Equals(uriA.Trim(), uriB.Trim(), StringComparison.OrdinalIgnoreCase);

        // Scheme: case-insensitive; sip ≠ sips
        if (!string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        // User: case-sensitive; for user=phone normalize visual separators
        var userA = a.User;
        var userB = b.User;
        var userParamA = GetUriParam(a.Params, "user");
        var userParamB = GetUriParam(b.Params, "user");
        var isPhone = string.Equals(userParamA, "phone", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(userParamB, "phone", StringComparison.OrdinalIgnoreCase);
        if (isPhone)
        {
            userA = NormalizePhoneUser(userA);
            userB = NormalizePhoneUser(userB);
            if (!string.Equals(userA, userB, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (!string.Equals(userA, userB, StringComparison.Ordinal))
        {
            return false;
        }

        // Host: case-insensitive
        if (!string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        // Port: resolve scheme default
        var defaultPort = string.Equals(a.Scheme, "sips", StringComparison.OrdinalIgnoreCase) ? 5061 : 5060;
        var portA = a.Port ?? defaultPort;
        var portB = b.Port ?? defaultPort;
        if (portA != portB)
            return false;

        // Transport: default is "udp" (RFC 3261 §19.1.4 example: transport=udp ≡ absent)
        var transportA = GetUriParam(a.Params, "transport") ?? "udp";
        var transportB = GetUriParam(b.Params, "transport") ?? "udp";
        if (!string.Equals(transportA, transportB, StringComparison.OrdinalIgnoreCase))
            return false;

        // Known parameters without a default: absent ≠ any explicit value
        foreach (var name in new[] { "maddr", "ttl", "user", "method" })
        {
            var pA = GetUriParam(a.Params, name);
            var pB = GetUriParam(b.Params, name);
            if (!string.Equals(pA, pB, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // lr: boolean parameter — presence matters, value does not
        var lrA = GetUriParam(a.Params, "lr") is not null;
        var lrB = GetUriParam(b.Params, "lr") is not null;
        if (lrA != lrB) return false;

        // Unknown parameters: any parameter on one side but not the other → not equal
        // (RFC 3261 §19.1.4: "A URI that includes an unknown parameter MUST NOT be less
        //  specific than one without that parameter.")
        var knownNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "transport", "maddr", "ttl", "user", "method", "lr" };
        var unknownA = ParseUnknownUriParams(a.Params, knownNames);
        var unknownB = ParseUnknownUriParams(b.Params, knownNames);
        if (unknownA.Count != unknownB.Count) return false;
        foreach (var kv in unknownA)
        {
            if (!unknownB.TryGetValue(kv.Key, out var bVal)) return false;
            if (!string.Equals(kv.Value, bVal, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // URI headers — each header present in either URI must match
        if (!SipUriHeadersEqual(a.Headers, b.Headers))
            return false;

        return true;
    }

    /// <summary>
    /// Decomposes a SIP/SIPS URI (or name-addr) into its components.
    /// </summary>
    private static bool TryDecomposeSipUri(string uriOrNameAddr, out SipUriComponents result)
    {
        result = default;
        // Strip name-addr wrapper (angle brackets + optional display name) without
        // stripping URI parameters — ExtractUriFromNameAddr must not be used here
        // because it truncates at the first ';', destroying URI parameters.
        var trimmed = uriOrNameAddr.AsSpan().Trim();
        var left  = trimmed.IndexOf('<');
        var right = trimmed.LastIndexOf('>');
        var raw = (left >= 0 && right > left)
            ? trimmed[(left + 1)..right].Trim().ToString()
            : trimmed.ToString();

        string scheme;
        string rest;
        if (raw.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "sips";
            rest = raw[5..];
        }
        else if (raw.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "sip";
            rest = raw[4..];
        }
        else
        {
            return false;
        }

        // Split headers ('?')
        var headerSep = rest.IndexOf('?');
        var headersStr = headerSep >= 0 ? rest[(headerSep + 1)..] : string.Empty;
        if (headerSep >= 0) rest = rest[..headerSep];

        // Split parameters (first ';')
        var paramSep = rest.IndexOf(';');
        var paramsStr = paramSep >= 0 ? rest[(paramSep + 1)..] : string.Empty;
        if (paramSep >= 0) rest = rest[..paramSep];

        // User@host
        var atIdx = rest.IndexOf('@');
        var user = atIdx >= 0 ? rest[..atIdx] : string.Empty;
        var hostPort = atIdx >= 0 ? rest[(atIdx + 1)..] : rest;

        // Host + port
        int? port = null;
        string host;
        if (hostPort.StartsWith("[", StringComparison.Ordinal))
        {
            var end = hostPort.IndexOf(']');
            host = end > 0 ? hostPort[1..end] : hostPort;
            if (end > 0 && end + 1 < hostPort.Length && hostPort[end + 1] == ':'
                && int.TryParse(hostPort[(end + 2)..], out var p6))
                port = p6;
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var p))
            {
                host = hostPort[..colon];
                port = p;
            }
            else
            {
                host = hostPort;
            }
        }

        result = new SipUriComponents(scheme, user, host, port, paramsStr, headersStr);
        return !string.IsNullOrWhiteSpace(host);
    }

    private static System.Collections.Generic.Dictionary<string, string?> ParseUnknownUriParams(
        string paramsStr,
        System.Collections.Generic.HashSet<string> knownNames)
    {
        var result = new System.Collections.Generic.Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(paramsStr)) return result;
        foreach (var seg in paramsStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = seg.IndexOf('=');
            var segName = (eq >= 0 ? seg[..eq] : seg).Trim();
            if (knownNames.Contains(segName)) continue;
            result[segName] = eq >= 0 ? seg[(eq + 1)..].Trim() : null;
        }
        return result;
    }

    private static string? GetUriParam(string paramsStr, string name)
    {
        if (string.IsNullOrWhiteSpace(paramsStr)) return null;
        foreach (var segment in paramsStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            var segName = eq >= 0 ? segment[..eq] : segment;
            if (!string.Equals(segName.Trim(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            return eq >= 0 ? segment[(eq + 1)..].Trim() : string.Empty;
        }
        return null;
    }

    private static string NormalizePhoneUser(string user)
    {
        // Remove visual separators per RFC 3966 §3: '-', '.', '(', ')'
        var sb = new System.Text.StringBuilder(user.Length);
        foreach (var ch in user)
            if (ch != '-' && ch != '.' && ch != '(' && ch != ')')
                sb.Append(ch);
        return sb.ToString();
    }

    private static bool SipUriHeadersEqual(string headersA, string headersB)
    {
        if (string.IsNullOrEmpty(headersA) && string.IsNullOrEmpty(headersB))
            return true;

        static System.Collections.Generic.Dictionary<string, string> Parse(string h)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(h)) return dict;
            foreach (var pair in h.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                var k = eq >= 0 ? pair[..eq] : pair;
                var v = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
                dict[k] = v;
            }
            return dict;
        }

        var a = Parse(headersA);
        var b = Parse(headersB);
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var val)) return false;
            if (!string.Equals(kv.Value, val, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }


    // -----------------------------------------------------------------------
    // §19.1.6 — Relating SIP URIs and tel URLs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a tel URI (RFC 3966) to a SIP URI per RFC 3261 §19.1.6.
    /// Global numbers (+E.164) are normalized by stripping visual separators.
    /// Local numbers are passed through as-is.
    /// Returns false when <paramref name="telUri"/> is not a tel: URI.
    /// </summary>
    /// <param name="telUri">tel URI, e.g. <c>tel:+1-800-555-0100</c> or <c>tel:555-0100</c>.</param>
    /// <param name="domain">Host part for the resulting SIP URI, e.g. <c>pbx.example.org</c>.</param>
    /// <param name="sipUri">Resulting SIP URI, e.g. <c>sip:+18005550100@pbx.example.org;user=phone</c>.</param>
    public static bool TryTelUriToSipUri(string? telUri, string domain, out string sipUri)
    {
        sipUri = string.Empty;
        if (string.IsNullOrWhiteSpace(telUri) || string.IsNullOrWhiteSpace(domain))
            return false;

        var raw = telUri.Trim();
        if (!raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return false;

        var number = raw[4..];

        // Strip phone-context and other parameters (";phone-context=...")
        var paramIdx = number.IndexOf(';');
        if (paramIdx >= 0)
            number = number[..paramIdx];

        number = number.Trim();
        if (number.Length == 0)
            return false;

        // Global number: starts with '+'; normalize by removing visual separators
        if (number[0] == '+')
        {
            var normalized = new System.Text.StringBuilder(number.Length);
            normalized.Append('+');
            for (var i = 1; i < number.Length; i++)
            {
                var ch = number[i];
                if (ch is '-' or '.' or '(' or ')')
                    continue; // strip visual separator
                normalized.Append(ch);
            }
            number = normalized.ToString();
        }

        sipUri = $"sip:{number}@{domain};user=phone";
        return true;
    }
}
