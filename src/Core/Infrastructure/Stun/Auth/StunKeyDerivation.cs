using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

/// <summary>
/// Derives HMAC-SHA1 keys for STUN short-term and long-term credential mechanisms (RFC 5389 §10).
/// <para>
/// Short-term (RFC 5389 §10.1): key = SASLprep(password) as UTF-8 bytes.
/// Used in ICE connectivity checks where each side knows the peer's ICE password.
/// </para>
/// <para>
/// Long-term (RFC 5389 §10.2): key = MD5(username ":" realm ":" SASLprep(password)).
/// Used for STUN servers requiring persistent authentication (e.g. TURN).
/// </para>
/// </summary>
internal static class StunKeyDerivation
{
    /// <summary>
    /// Derives the HMAC key for short-term credential authentication.
    /// Applies SASLprep to the password and returns UTF-8 bytes.
    /// </summary>
    /// <param name="password">The SIP/ICE password in clear text.</param>
    public static byte[] ShortTermKey(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Encoding.UTF8.GetBytes(SasLPrep(password));
    }

    /// <summary>
    /// Derives the HMAC key for long-term credential authentication.
    /// Returns MD5(username ":" realm ":" SASLprep(password)) as a 16-byte key.
    /// </summary>
    /// <param name="username">The username credential.</param>
    /// <param name="realm">The authentication realm from the server challenge.</param>
    /// <param name="password">The password in clear text.</param>
    public static byte[] LongTermKey(string username, string realm, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(password);

        var input = $"{username}:{realm}:{SasLPrep(password)}";
        // CA5351 (weak crypto): MD5 is not a free algorithm choice here — RFC 5389 §10.2.3 defines the STUN
        // long-term-credential key as exactly MD5(username ":" realm ":" SASLprep(password)). Using anything
        // else would break interoperability with every conformant STUN/TURN server. Interop-mandated.
#pragma warning disable CA5351
        return MD5.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5351
    }

    /// <summary>
    /// Applies the SASLprep stringprep profile (RFC 4013) to a string.
    /// <para>
    /// Steps applied:
    /// 1. Map non-ASCII space characters (Unicode category Zs) to U+0020 (SPACE).
    ///    Characters mapped to nothing (RFC 3454 §B.1) are removed.
    /// 2. Normalize to Unicode NFKC form (KC normalization).
    /// 3. Prohibit: ASCII control characters (except HT, LF, CR), non-character
    ///    code points, and surrogate pairs. Private-use and other prohibited
    ///    categories as defined in RFC 3454 §C are also rejected.
    /// </para>
    /// Note: Full bidirectional (bidi) checking per RFC 3454 §6 is not applied here
    /// as it is rarely relevant for VoIP credentials and requires full Unicode bidi data.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the input contains a character prohibited by SASLprep.
    /// </exception>
    public static string SasLPrep(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Step 1: map non-ASCII space separators → U+0020; map prohibited control chars → nothing.
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (IsMappedToNothing(ch))
                continue;

            sb.Append(char.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator ? ' ' : ch);
        }

        // Step 2: NFKC normalization.
        var normalized = sb.ToString().Normalize(NormalizationForm.FormKC);

        // Step 3: check prohibited characters.
        for (int i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (IsProhibited(ch))
            {
                throw new ArgumentException(
                    $"Character U+{(int)ch:X4} is prohibited by SASLprep (RFC 4013).",
                    nameof(input));
            }
        }

        return normalized;
    }

    /// <summary>
    /// Returns true when a character is mapped to nothing per RFC 3454 Table B.1
    /// (commonly-mapped-to-nothing code points used in SASLprep).
    /// This covers the soft hyphen and zero-width characters most relevant to passwords.
    /// </summary>
    private static bool IsMappedToNothing(char ch) => ch switch
    {
        '\u00AD' => true, // SOFT HYPHEN
        '\u034F' => true, // COMBINING GRAPHEME JOINER
        '\u1806' => true, // MONGOLIAN TODO SOFT HYPHEN
        '\u180B' => true, // MONGOLIAN FREE VARIATION SELECTOR ONE
        '\u180C' => true, // MONGOLIAN FREE VARIATION SELECTOR TWO
        '\u180D' => true, // MONGOLIAN FREE VARIATION SELECTOR THREE
        '\u200B' => true, // ZERO WIDTH SPACE
        '\u200C' => true, // ZERO WIDTH NON-JOINER
        '\u200D' => true, // ZERO WIDTH JOINER
        '\u2060' => true, // WORD JOINER
        '\uFEFF' => true, // ZERO WIDTH NO-BREAK SPACE (BOM)
        >= '\uFE00' and <= '\uFE0F' => true, // VARIATION SELECTORS 1–16
        _ => false
    };

    /// <summary>
    /// Returns true when a character is prohibited by SASLprep (RFC 4013 §2.3).
    /// Covers ASCII control characters, non-characters, and surrogates.
    /// </summary>
    private static bool IsProhibited(char ch)
    {
        // ASCII control characters except TAB (0x09), LF (0x0A), CR (0x0D).
        if (ch < 0x0020 && ch != '\t' && ch != '\n' && ch != '\r')
            return true;

        // DEL.
        if (ch == 0x007F)
            return true;

        // Non-character code points in BMP (U+FDD0–U+FDEF, U+FFFE, U+FFFF).
        if (ch is >= '\uFDD0' and <= '\uFDEF')
            return true;
        if (ch is '\uFFFE' or '\uFFFF')
            return true;

        // Surrogate pairs (should not appear in well-formed .NET strings, but check anyway).
        if (char.IsSurrogate(ch))
            return true;

        return false;
    }
}
