namespace CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

/// <summary>
/// SIP-URI semantics: percent-escaping of the user part (RFC 3261 §19.1.2), URI comparison (§19.1.4), and the
/// tel→SIP mapping (§19.1.6). Split out of <see cref="SipProtocol"/>, whose remaining job — branch/tag/Call-ID
/// generation, status classes, Via and CSeq parsing — is message plumbing rather than address semantics.
/// </summary>
/// <remarks>
/// These rules are the ones in this area that look obvious and are not, which is why they earn their own home:
/// a URI omitting a component with a default value does <em>not</em> match one that states it, while an unknown
/// parameter present on only one side is ignored rather than disqualifying. Getting either backwards decides
/// whether a call is answered or turned away — see <c>SipUriComparisonTests</c>, which pins every worked example
/// §19.1.4 publishes.
/// </remarks>
internal static class SipUriProtocol
{
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

        // Scheme: case-insensitive; sip ≠ sips (a cleartext identity must never match a TLS-required one).
        if (!string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        // User (with password, as decomposed): case-sensitive, but a percent-escape of an unreserved character
        // is equivalent to the character itself (§19.1.4 "Characters other than those in the reserved set are
        // equivalent to their ""HEX HEX encoding"). Only unreserved escapes are decoded — decoding a reserved
        // character would change what the component means, not just how it is written.
        var userA = NormalizeUnreservedEscapes(a.User);
        var userB = NormalizeUnreservedEscapes(b.User);
        var userParamA = GetUriParam(a.Params, "user");
        var userParamB = GetUriParam(b.Params, "user");
        if (string.Equals(userParamA, "phone", StringComparison.OrdinalIgnoreCase)
            || string.Equals(userParamB, "phone", StringComparison.OrdinalIgnoreCase))
        {
            // user=phone: the user part is a telephone-subscriber, whose visual separators carry no meaning
            // (RFC 3966 §3). Beyond §19.1.4 proper, but comparing +49-30-1 against +49301 as different numbers
            // would be wrong in every direction that matters.
            if (!string.Equals(NormalizePhoneUser(userA), NormalizePhoneUser(userB), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (!string.Equals(userA, userB, StringComparison.Ordinal))
        {
            return false;
        }

        // Host: case-insensitive.
        if (!string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        // Port: compared as stated, NOT resolved to the scheme default. §19.1.4 is explicit and lists it as a
        // worked example — "sip:bob@biloxi.com" and "sip:bob@biloxi.com:5060" are NOT equivalent, because the
        // one that omits the port can still resolve elsewhere. Defaulting both to 5060 would equate an identity
        // that is pinned to a port with one that is not.
        if (a.Port != b.Port)
            return false;

        // Parameters present in BOTH must match; presence on only one side is decided per parameter below.
        // Comparison is case-insensitive for names and values (§19.1.4: only the userinfo is case-sensitive).
        foreach (var name in OneSidedSignificantUriParams)
        {
            // transport, user, ttl, method and maddr change how the URI resolves, so stating one is never the
            // same as leaving it out — even when the stated value is the default (§19.1.4, and its
            // "sip:bob@biloxi.com" vs "sip:bob@biloxi.com;transport=udp" example).
            if (!string.Equals(GetUriParam(a.Params, name), GetUriParam(b.Params, name), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Every other parameter — including ones this stack does not know — matters only when BOTH URIs carry
        // it. "All other uri-parameters appearing in only one URI are ignored when comparing the URIs": that is
        // what makes "sip:carol@chicago.com" equivalent to "sip:carol@chicago.com;newparam=5".
        var otherA = ParseUnknownUriParams(a.Params, OneSidedSignificantUriParams);
        var otherB = ParseUnknownUriParams(b.Params, OneSidedSignificantUriParams);
        foreach (var parameter in otherA)
        {
            if (otherB.TryGetValue(parameter.Key, out var valueB)
                && !string.Equals(parameter.Value, valueB, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // URI headers are never ignored: one present in either URI must be present in both and match.
        return SipUriHeadersEqual(a.Headers, b.Headers);
    }

    // The uri-parameters whose mere presence is significant (RFC 3261 §19.1.4): each one changes how the URI
    // resolves, so a URI that states it never matches one that omits it. Every other parameter is ignored when
    // it appears on one side only.
    private static readonly System.Collections.Generic.HashSet<string> OneSidedSignificantUriParams =
        new(StringComparer.OrdinalIgnoreCase) { "transport", "user", "ttl", "method", "maddr" };

    // Decodes the percent-escapes that stand for UNRESERVED characters (RFC 3261 §25.1: alphanum / mark), which
    // §19.1.4 declares equivalent to the character itself. Escapes of reserved characters are left alone: "%40"
    // is not an "@" for comparison purposes, it is an at-sign inside a component.
    private static string NormalizeUnreservedEscapes(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('%', StringComparison.Ordinal))
            return value ?? string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                var decoded = (char)Convert.ToInt32(value.Substring(i + 1, 2), 16);
                if (IsUnreserved(decoded))
                {
                    builder.Append(decoded);
                    i += 2;
                    continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')';

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
