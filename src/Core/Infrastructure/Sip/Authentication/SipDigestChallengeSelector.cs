using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using System.Buffers;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

/// <summary>
/// Selects SIP Digest challenge headers and the corresponding authorization header names.
/// </summary>
internal static class SipDigestChallengeSelector
{
    private static readonly SearchValues<char> AlgorithmTokenTerminators = SearchValues.Create(",\" \t\r\n");

    /// <summary>
    /// Attempts to read a digest challenge from one SIP response.
    /// When multiple challenges are present, selects the strongest supported algorithm
    /// per RFC 7616 §4 (SHA-512-256 > SHA-256 > MD5).
    /// Returns true when a supported challenge is present.
    /// </summary>
    public static bool TrySelect(
        SipResponse response,
        out string challengeHeader,
        out string authorizationHeaderName)
    {
        if (TrySelectStrongest(response.HeaderValues("WWW-Authenticate"), out var best))
        {
            challengeHeader = best;
            authorizationHeaderName = "Authorization";
            return true;
        }

        if (TrySelectStrongest(response.HeaderValues("Proxy-Authenticate"), out best))
        {
            challengeHeader = best;
            authorizationHeaderName = "Proxy-Authorization";
            return true;
        }

        challengeHeader = string.Empty;
        authorizationHeaderName = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true when the digest challenge indicates a stale nonce.
    /// </summary>
    public static bool IsStaleChallenge(string challengeHeader) =>
        !string.IsNullOrWhiteSpace(challengeHeader)
        && (challengeHeader.Contains("stale=true", StringComparison.OrdinalIgnoreCase)
            || challengeHeader.Contains("stale=\"true\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// RFC 7616 §4: among all Digest challenges in one header category, pick the one
    /// with the strongest supported algorithm. Non-Digest schemes are ignored.
    /// </summary>
    private static bool TrySelectStrongest(IEnumerable<string> headerValues, out string best)
    {
        best = string.Empty;
        var bestScore = -1;

        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!value.TrimStart().StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) continue;

            var score = AlgorithmStrength(ExtractAlgorithm(value));
            if (score > bestScore)
            {
                bestScore = score;
                best = value;
            }
        }

        return bestScore >= 0;
    }

    /// <summary>
    /// Extracts the algorithm parameter value from a Digest challenge string.
    /// Returns "MD5" when absent (RFC 7616 default).
    /// </summary>
    private static string ExtractAlgorithm(string challenge)
    {
        // Simple linear scan: find "algorithm=" token (case-insensitive)
        var span = challenge.AsSpan();
        var idx = span.IndexOf("algorithm=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "MD5";

        var after = span[(idx + "algorithm=".Length)..].TrimStart();
        // Strip surrounding quotes if present
        if (after.StartsWith("\"", StringComparison.Ordinal))
            after = after[1..];
        // Read until comma, quote, or whitespace
        var end = after.IndexOfAny(AlgorithmTokenTerminators);
        return (end >= 0 ? after[..end] : after).Trim().ToString();
    }

    /// <summary>
    /// Returns a numeric strength score for a Digest algorithm token.
    /// Higher is stronger. Only algorithms the authenticator can actually compute score >= 0;
    /// unsupported algorithms score -1 so the selector never picks a challenge that would fail at
    /// the hashing step when a weaker but supported challenge is also on offer. This list MUST stay
    /// in sync with <c>SipDigestAuthentication.TryResolveAlgorithm</c>.
    /// </summary>
    private static int AlgorithmStrength(string algorithm) =>
        algorithm.Trim().ToUpperInvariant() switch
        {
            "SHA-512-256"      or "SHA-512/256"      => 30,
            "SHA-512-256-SESS" or "SHA-512/256-SESS" => 29,
            "SHA-256"                                 => 20,
            "SHA-256-SESS"                            => 19,
            "MD5"                                     => 10,
            "MD5-SESS"                                => 9,
            _                                         => -1
        };
}
