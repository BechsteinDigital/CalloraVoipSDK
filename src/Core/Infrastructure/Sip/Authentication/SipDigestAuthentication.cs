using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

/// <summary>
/// Minimal SIP Digest authentication helper for REGISTER and INVITE challenges.
/// </summary>
internal sealed class SipDigestAuthentication : ISipDigestAuthenticator
{
    /// <summary>
    /// Attempts to build an Authorization header value from a Digest challenge.
    /// </summary>
    public bool TryCreateAuthorizationHeader(
        string? challengeHeader,
        string username,
        string password,
        string method,
        string requestUri,
        int nonceCount,
        out string authorizationHeader)
    {
        authorizationHeader = string.Empty;
        if (string.IsNullOrWhiteSpace(challengeHeader)) return false;
        if (!challengeHeader.TrimStart().StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return false;

        var parameters = ParseChallengeParameters(challengeHeader);
        if (!parameters.TryGetValue("realm", out var realm)) return false;
        if (!parameters.TryGetValue("nonce", out var nonce)) return false;

        var algorithm = parameters.TryGetValue("algorithm", out var algorithmValue)
            ? algorithmValue
            : "MD5";
        if (!TryResolveAlgorithm(algorithm, out var baseAlgorithm, out var useSessionAlgorithm))
            return false;

        var qop = parameters.TryGetValue("qop", out var qopValue)
            ? ChooseQop(qopValue)
            : null;

        var cnonce = SipProtocol.NewTag();
        var nc = nonceCount.ToString("x8");
        if (!ProtocolCommonUtilities.TryHashHexLower($"{username}:{realm}:{password}", baseAlgorithm, out var ha1Base))
            return false;
        var ha1 = useSessionAlgorithm
            ? ProtocolCommonUtilities.TryHashHexLower($"{ha1Base}:{nonce}:{cnonce}", baseAlgorithm, out var sessionHa1)
                ? sessionHa1
                : string.Empty
            : ha1Base;
        if (ha1.Length == 0)
            return false;

        if (!ProtocolCommonUtilities.TryHashHexLower($"{method}:{requestUri}", baseAlgorithm, out var ha2))
            return false;
        var responseInput = qop is null
            ? $"{ha1}:{nonce}:{ha2}"
            : $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}";
        if (!ProtocolCommonUtilities.TryHashHexLower(responseInput, baseAlgorithm, out var response))
            return false;

        var builder = new StringBuilder("Digest ");
        builder.Append($"username=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(username)}\", ");
        builder.Append($"realm=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(realm)}\", ");
        builder.Append($"nonce=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(nonce)}\", ");
        builder.Append($"uri=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(requestUri)}\", ");
        builder.Append($"response=\"{response}\"");
        if (!string.IsNullOrWhiteSpace(algorithm))
            builder.Append($", algorithm={algorithm}");
        if (parameters.TryGetValue("opaque", out var opaque))
            builder.Append($", opaque=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(opaque)}\"");
        if (qop is not null)
        {
            builder.Append($", qop={qop}");
            builder.Append($", nc={nc}");
            builder.Append($", cnonce=\"{ProtocolCommonUtilities.EscapeQuotedHeaderValue(cnonce)}\"");
        }

        authorizationHeader = builder.ToString();
        return true;
    }

    /// <summary>
    /// Parses a Digest challenge into a parameter dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseChallengeParameters(string challengeHeader)
    {
        var challenge = challengeHeader.Trim();
        var prefixIndex = challenge.IndexOf(' ');
        if (prefixIndex >= 0)
            challenge = challenge[(prefixIndex + 1)..];

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(challenge))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0) continue;
            var key = token[..separator].Trim();
            var value = token[(separator + 1)..].Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                value = value[1..^1];
            map[key] = value;
        }

        return map;
    }

    /// <summary>
    /// Chooses a supported qop mode from a server-provided qop list.
    /// </summary>
    private static string? ChooseQop(string qop) =>
        ProtocolCommonUtilities.ContainsToken(qop, "auth") ? "auth" : null;

    /// <summary>
    /// Resolves supported digest algorithm and whether "-sess" mode is active.
    /// </summary>
    private static bool TryResolveAlgorithm(
        string algorithm,
        out string baseAlgorithm,
        out bool useSessionAlgorithm)
    {
        baseAlgorithm = "MD5";
        useSessionAlgorithm = false;
        var normalized = algorithm.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "MD5":
                baseAlgorithm = "MD5";
                return true;
            case "MD5-SESS":
                baseAlgorithm = "MD5";
                useSessionAlgorithm = true;
                return true;
            case "SHA-256":
                baseAlgorithm = "SHA-256";
                return true;
            case "SHA-256-SESS":
                baseAlgorithm = "SHA-256";
                useSessionAlgorithm = true;
                return true;
            // SHA-512-256 (RFC 7616) is intentionally not advertised: .NET's managed crypto has no
            // SHA-512/256 primitive (unlike the OpenSSL CLI), so it cannot be computed here.
            // Reporting it unsupported (false) is honest, rather than resolving it and then failing
            // at the hash step. Registrars that require it are extremely rare in practice.
            default:
                return false;
        }
    }
}
