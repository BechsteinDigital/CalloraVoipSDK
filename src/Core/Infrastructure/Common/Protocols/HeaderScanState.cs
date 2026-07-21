namespace CalloraVoipSdk.Core.Infrastructure.Common.Protocols;

/// <summary>
/// Stateful, single-pass scanner for RFC 3261 §7.3.1 header values. Advanced one character at a time via
/// <see cref="Consume"/>, it tracks whether the cursor currently sits inside a quoted-string (<c>"…"</c>, with
/// the <c>\</c> quoted-pair escape per RFC 3261 §25.1) and the name-addr angle-bracket depth (<c>&lt;…&gt;</c>).
/// Callers use it to tell a <em>structural</em> delimiter — a <c>","</c>, <c>";"</c> or <c>"="</c> at the top
/// level — apart from the same character appearing literally inside a display name or a <c>&lt;URI&gt;</c>.
/// This is a mutable value type; scan a header by declaring one instance and feeding it every character in order.
/// </summary>
internal struct HeaderScanState
{
    private bool _escaped;

    /// <summary>Whether the cursor is currently inside a quoted-string (between unescaped double quotes).</summary>
    public bool InQuotedString { get; private set; }

    /// <summary>The current name-addr angle-bracket nesting depth (<c>&lt;…&gt;</c>), never negative.</summary>
    public int AngleBracketDepth { get; private set; }

    /// <summary>
    /// Advances the scanner over one character and returns whether that character is <em>structural</em>: it sits
    /// at the top level (outside any quoted-string and outside all angle brackets) and is not itself a quote or
    /// angle-bracket toggle. Only a structural character can be a header delimiter such as <c>","</c>, <c>";"</c>
    /// or <c>"="</c>. A quote toggle, an angle bracket, and every character inside a quoted-string or inside angle
    /// brackets (including the second half of a <c>\</c> quoted-pair) return <see langword="false"/>.
    /// </summary>
    /// <param name="c">The next character of the header value, fed strictly in order.</param>
    public bool Consume(char c)
    {
        if (_escaped)
        {
            // Second half of a quoted-pair (\x): always literal, never a delimiter or a quote toggle.
            _escaped = false;
            return false;
        }

        if (InQuotedString)
        {
            if (c == '\\') { _escaped = true; return false; }
            if (c == '"') { InQuotedString = false; return false; }
            return false; // any other character inside a quoted-string is literal.
        }

        switch (c)
        {
            case '"':
                InQuotedString = true;
                return false;
            case '<':
                AngleBracketDepth++;
                return false;
            case '>':
                if (AngleBracketDepth > 0) AngleBracketDepth--;
                return false;
            default:
                return AngleBracketDepth == 0; // structural only at the top angle-bracket level.
        }
    }
}
