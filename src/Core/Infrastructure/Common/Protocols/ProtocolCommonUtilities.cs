using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Protocols;

/// <summary>
/// Shared low-level helpers for protocol parsing and header construction.
/// These helpers are intentionally protocol-agnostic and reusable across SIP/RTP/SRTP modules.
/// </summary>
internal static class ProtocolCommonUtilities
{
    /// <summary>
    /// Splits a comma-separated header value list while preserving commas that fall inside a quoted string
    /// (<c>"…"</c>) or a name-addr's angle brackets (<c>&lt;…&gt;</c>). This is the RFC 3261 §7.3.1 rule for
    /// combining/splitting multiple header field values: a comma inside a display name or a <c>&lt;URI&gt;</c>
    /// (which can itself carry commas in its parameters/headers) is not a value delimiter.
    /// </summary>
    public static IEnumerable<string> SplitCommaSeparatedRespectingQuotes(string value)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var bracketDepth = 0;
        foreach (var ch in value)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }

            if (!inQuotes && ch == '<')
            {
                bracketDepth++;
                current.Append(ch);
                continue;
            }

            if (!inQuotes && ch == '>')
            {
                if (bracketDepth > 0) bracketDepth--;
                current.Append(ch);
                continue;
            }

            if (ch == ',' && !inQuotes && bracketDepth == 0)
            {
                var token = current.ToString().Trim();
                if (token.Length > 0) yield return token;
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>
    /// Returns true when a comma-separated token list contains a specific token.
    /// </summary>
    public static bool ContainsToken(string value, string token)
    {
        foreach (var candidate in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = candidate.Trim().Trim('"');
            if (normalized.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Computes a lowercase hexadecimal MD5 digest string.
    /// </summary>
    public static string Md5HexLower(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes lowercase hexadecimal hash text for supported digest algorithms.
    /// </summary>
    public static bool TryHashHexLower(
        string value,
        string algorithm,
        out string hashHexLower)
    {
        hashHexLower = string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        var normalized = algorithm.Trim().ToUpperInvariant();
        byte[] hash = normalized switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA-256" => SHA256.HashData(bytes),
            "SHA-512-256" or "SHA-512/256" => Sha512_256(bytes),
            _ => Array.Empty<byte>()
        };
        if (hash.Length == 0)
            return false;

        hashHexLower = Convert.ToHexString(hash).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Computes SHA-512/256 (RFC 8760 / FIPS 180-4 §5.3.6). .NET exposes no managed SHA-512/256
    /// primitive — it is a distinct algorithm with its own initial hash values, not a truncation of
    /// SHA-512 — so it is computed with the BouncyCastle digest already referenced for DTLS.
    /// </summary>
    private static byte[] Sha512_256(byte[] bytes)
    {
        var digest = new Sha512tDigest(256);
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    /// <summary>
    /// Escapes a value for inclusion in a quoted header parameter.
    /// </summary>
    public static string EscapeQuotedHeaderValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}
