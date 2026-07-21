using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

/// <summary>
/// Tracks the Digest nonce-count (nc) for one authentication flow so it is coupled to the nonce (RFC 7616
/// §3.4, CF-042): nc is <c>00000001</c> for the first use of a nonce and increments only while the <em>same</em>
/// nonce is reused; a server-issued new nonce (a fresh or stale-refreshed challenge) resets nc to 1. Feeding an
/// ever-incrementing nc across challenges that each carry a new nonce makes a strict server reject the retry.
/// One instance is held per auth-retry loop (REGISTER/INVITE/SUBSCRIBE/in-dialog).
/// </summary>
internal sealed class SipNonceCounter
{
    private string? _nonce;
    private int _count;

    /// <summary>
    /// Returns the nonce-count to use for the next authenticated request against the given Digest challenge,
    /// resetting to 1 when the challenge carries a different nonce than the previous one and incrementing when it
    /// carries the same nonce. A challenge without a readable nonce is treated as unchanged.
    /// </summary>
    /// <param name="challengeHeader">The <c>WWW-Authenticate</c>/<c>Proxy-Authenticate</c> Digest challenge value.</param>
    public int NextFor(string? challengeHeader)
    {
        var nonce = ReadNonce(challengeHeader);
        if (!string.Equals(nonce, _nonce, StringComparison.Ordinal))
        {
            _nonce = nonce;
            _count = 1;
        }
        else
        {
            _count++;
        }

        return _count;
    }

    // Reads the nonce parameter from a Digest challenge (mirrors the authenticator's challenge parse).
    private static string? ReadNonce(string? challengeHeader)
    {
        if (string.IsNullOrWhiteSpace(challengeHeader))
            return null;

        var challenge = challengeHeader.Trim();
        var prefixIndex = challenge.IndexOf(' ');
        if (prefixIndex >= 0)
            challenge = challenge[(prefixIndex + 1)..];

        foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(challenge))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0)
                continue;
            if (!token[..separator].Trim().Equals("nonce", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = token[(separator + 1)..].Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                value = value[1..^1];
            return value;
        }

        return null;
    }
}
