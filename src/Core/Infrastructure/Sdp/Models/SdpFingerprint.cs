namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// Parsed representation of an SDP <c>a=fingerprint</c> attribute (RFC 8122 / RFC 5763).
/// Used to convey the DTLS certificate fingerprint in SDP offer/answer.
/// </summary>
internal sealed class SdpFingerprint
{
    /// <summary>Hash algorithm token, e.g. <c>sha-256</c> or <c>sha-1</c>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Hex-encoded fingerprint value, colon-delimited, e.g. <c>AA:BB:CC:…</c>.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// Tries to parse the attribute value that follows <c>a=fingerprint:</c>.
    /// Returns <see langword="null"/> on malformed input.
    /// </summary>
    public static SdpFingerprint? TryParse(string attrValue)
    {
        if (string.IsNullOrWhiteSpace(attrValue))
            return null;

        var space = attrValue.IndexOf(' ');
        if (space <= 0 || space == attrValue.Length - 1)
            return null;

        var algorithm = attrValue[..space].Trim();
        var value = attrValue[(space + 1)..].Trim();

        // #160 P2-5: this used to accept any two tokens, so "a=fingerprint:garbage nope" parsed into a
        // fingerprint and the m-line came out DTLS-enabled. The certificate fingerprint is the ONLY thing
        // authenticating the DTLS peer (RFC 5763 §6.7.1) — a value that cannot be one must not present
        // itself as one, or the leg looks keyed while nothing was verified.
        //
        // Deliberately checks the *grammar* and the algorithm, not the digest length. Whether the value is
        // the RIGHT fingerprint is decided where it can actually be decided: against the peer's certificate
        // (DtlsFingerprintValidator, constant-time, fail-closed). A parser that also enforced per-algorithm
        // digest lengths would only duplicate that check, one layer too early to be authoritative.
        if (!IsKnownHashFunction(algorithm) || !IsColonHex(value))
            return null;

        return new SdpFingerprint { Algorithm = algorithm, Value = value };
    }

    // The hash functions RFC 8122 §5 registers. An algorithm outside this set cannot be computed over a
    // certificate at all, so a fingerprint claiming one can never be verified.
    private static bool IsKnownHashFunction(string algorithm) => algorithm.ToLowerInvariant()
        is "sha-1" or "sha-224" or "sha-256" or "sha-384" or "sha-512" or "md5" or "md2";

    // RFC 8122 §5 grammar: hex byte pairs separated by colons. Checked without allocating, since this runs
    // on every inbound offer and answer.
    private static bool IsColonHex(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value.Length % 3 != 2)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (i % 3 == 2)
            {
                if (value[i] != ':')
                    return false;
            }
            else if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Serializes to the attribute value string (without the leading <c>a=fingerprint:</c>).
    /// </summary>
    public string Serialize() => $"{Algorithm} {Value}";
}
