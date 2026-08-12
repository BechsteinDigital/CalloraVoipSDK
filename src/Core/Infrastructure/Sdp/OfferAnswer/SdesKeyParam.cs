namespace CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

/// <summary>
/// One parsed SDES <c>inline</c> key parameter (RFC 4568 §6.1):
/// <c>"inline:" key-salt ["|" lifetime] ["|" MKI ":" MKI-length]</c>.
/// <para>
/// Exists so the answerer can see what a <c>a=crypto</c> line actually asks for before answering it.
/// Splitting on <c>'|'</c> and keeping only the first field — the previous behaviour — silently drops
/// a lifetime and an MKI, which means answering a crypto tag whose parameters we do not implement
/// (#157 P2-4). With MKI that is not a cosmetic mismatch: the peer prefixes an MKI to every SRTP
/// packet's authentication portion, we read those bytes as ciphertext, and the negotiation looks
/// successful while media never decodes.
/// </para>
/// </summary>
internal readonly record struct SdesKeyParam
{
    private const string InlinePrefix = "inline:";

    /// <summary>The base64 master key concatenated with the master salt.</summary>
    public required string KeySalt { get; init; }

    /// <summary>
    /// The offered key lifetime as written on the wire (<c>2^31</c> or a decimal packet count), or
    /// <see langword="null"/> when the peer offered none. Not interpreted here.
    /// </summary>
    public string? Lifetime { get; init; }

    /// <summary>
    /// The offered MKI as <c>value:length</c>, or <see langword="null"/> when the peer offered none.
    /// </summary>
    public string? Mki { get; init; }

    /// <summary>
    /// Parses a single <c>inline</c> key parameter. Returns <see langword="false"/> for anything this
    /// SDK cannot answer honestly: a non-inline key method, a key-params list carrying more than one
    /// parameter (<c>";"</c>-separated, used for MKI key sets), an empty key-salt, or more fields than
    /// the grammar allows.
    /// </summary>
    /// <param name="keyParams">The <c>a=crypto</c> key-params field.</param>
    /// <param name="result">The parsed parameter when parsing succeeded.</param>
    public static bool TryParse(string? keyParams, out SdesKeyParam result)
    {
        result = default;
        if (string.IsNullOrEmpty(keyParams))
            return false;

        // A ";"-separated list carries several key parameters — used to offer an MKI key set. We
        // implement one key per direction, so a list is refused rather than silently reduced to its
        // first entry.
        if (keyParams.Contains(';', StringComparison.Ordinal))
            return false;

        if (!keyParams.StartsWith(InlinePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var fields = keyParams[InlinePrefix.Length..].Split('|');
        if (fields.Length > 3 || fields[0].Length == 0)
            return false;

        string? lifetime = null;
        string? mki = null;
        for (var i = 1; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length == 0)
                return false;

            // The MKI field is the one written as "value:length"; a lifetime never contains a colon
            // (RFC 4568 §6.1). Order is fixed — lifetime first — so a second lifetime is malformed.
            if (field.Contains(':', StringComparison.Ordinal))
            {
                if (mki is not null)
                    return false;
                mki = field;
            }
            else
            {
                if (lifetime is not null || mki is not null)
                    return false;
                lifetime = field;
            }
        }

        result = new SdesKeyParam { KeySalt = fields[0], Lifetime = lifetime, Mki = mki };
        return true;
    }
}
