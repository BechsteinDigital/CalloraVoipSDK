namespace CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

/// <summary>
/// Syntax rules for the host and port components of a SIP URI (RFC 3261 §25.1), used by
/// <see cref="SipProtocol.TryParseSipUri"/>. That routine gates the ingress
/// (<c>SipIngressRequestPolicy</c>) and feeds route resolution, so what it accepts has to be a host and a
/// port — not merely something a lenient parser did not choke on (#158 P3-15).
/// </summary>
internal static class SipUriSyntax
{
    /// <summary>
    /// Longest legal host, matching DNS: 253 characters.
    /// </summary>
    private const int MaxHostLength = 253;

    /// <summary>
    /// Longest legal DNS label.
    /// </summary>
    private const int MaxLabelLength = 63;

    /// <summary>
    /// Parses a SIP URI port: ASCII digits only, 1–65535 (<c>port = 1*DIGIT</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="int.TryParse(string, out int)"/> also accepts a sign, surrounding whitespace, and values no
    /// socket can carry; those travelled on into <c>new IPEndPoint(address, port)</c> during route resolution.
    /// </remarks>
    /// <param name="text">Candidate port text, without the separating colon.</param>
    /// <param name="port">The parsed port when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the text is a valid SIP URI port.</returns>
    public static bool TryParsePort(string text, out int port)
    {
        port = 0;
        // Five digits is the widest a port can be, which also keeps the accumulator below overflow.
        if (text.Length is 0 or > 5)
            return false;

        var value = 0;
        foreach (var digit in text)
        {
            if (!char.IsAsciiDigit(digit))
                return false;

            value = (value * 10) + (digit - '0');
        }

        if (value is < 1 or > 65535)
            return false;

        port = value;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="host"/> is a valid unbracketed host — a hostname or IPv4 address.
    /// Bracketed IPv6 references are validated by the caller against <c>IPAddress</c> instead.
    /// </summary>
    /// <remarks>
    /// Deliberately the ABNF's character set rather than its full label grammar: <c>toplabel</c> requires a
    /// leading ALPHA, which would reject the all-numeric labels real deployments use, and underscores appear
    /// in the wild. The job here is to keep non-hosts out of route resolution, not to be a hostname registry.
    /// </remarks>
    public static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > MaxHostLength)
            return false;

        // A single trailing dot is the FQDN root label.
        var value = host.EndsWith('.') ? host[..^1] : host;
        if (value.Length == 0)
            return false;

        var labelLength = 0;
        foreach (var character in value)
        {
            if (character == '.')
            {
                if (labelLength == 0)
                    return false;

                labelLength = 0;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return false;

            if (++labelLength > MaxLabelLength)
                return false;
        }

        return labelLength > 0;
    }
}
