namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// Rejects text that cannot appear inside an SDP line (#160 P3-19).
/// </summary>
/// <remarks>
/// SDP is line-oriented (RFC 8866 §5): a value carrying CR or LF does not produce a malformed
/// attribute, it produces <em>additional lines</em>. A track id of
/// <c>stream\r\na=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:…</c> writes a crypto attribute the caller
/// never asked for, and the peer has no way to tell it apart from one we meant.
///
/// That matters here more than in a plain UA: values such as <c>a=msid</c>, RIDs and codec names come
/// from SDK configuration, and in a hosted setting that configuration can originate from an API
/// request rather than a developer's source file.
///
/// SIPSorcery does not guard this — it appends <c>SessionName</c> and the other text fields straight
/// into the output and assumes the input is trustworthy. This is one of the places where matching the
/// reference is not the goal.
///
/// The value is rejected rather than stripped: silently removing the newline would send a description
/// that differs from what the caller asked for, and a caller passing a newline has a bug worth
/// hearing about.
/// </remarks>
internal static class SdpTextGuard
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged, or throws when it cannot sit inside one SDP line.
    /// </summary>
    /// <exception cref="FormatException">The value contains CR, LF, NUL or another control character.</exception>
    public static string Line(string? value, string field)
    {
        if (value is null)
            return string.Empty;

        foreach (var c in value)
        {
            // Control characters other than the ones SDP itself uses as separators have no meaning
            // inside a line; CR and LF end it. Both are rejected on the same grounds.
            if (char.IsControl(c))
            {
                throw new FormatException(
                    $"SDP field '{field}' contains a control character (U+{(int)c:X4}) and cannot be written to a line.");
            }
        }

        return value;
    }

    /// <summary>
    /// True when the value is safe to write into an SDP line.
    /// </summary>
    public static bool IsLineSafe(string? value)
    {
        if (value is null)
            return true;

        foreach (var c in value)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }
}
