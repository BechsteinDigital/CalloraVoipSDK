namespace CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

/// <summary>
/// Encapsulates SIP header row combination and classification rules.
/// </summary>
internal static class SipHeaderRowRules
{
    private static readonly HashSet<string> NonCombinableHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "WWW-Authenticate",
        "Proxy-Authenticate",
        "Content-Length"
    };

    private static readonly HashSet<string> CommaCombinableHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "Alert-Info",
        "Allow",
        "Allow-Events",
        "Call-Info",
        "Contact",
        "Content-Encoding",
        "Content-Language",
        "Error-Info",
        "In-Reply-To",
        "Proxy-Require",
        "Record-Route",
        "Require",
        "Route",
        "Supported",
        "Unsupported",
        "Via",
        "Warning"
    };

    private static readonly HashSet<string> RequestOnlyHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Call-Info",
        // Expires is valid in responses (REGISTER 200 OK top-level, RFC 3261 §10.3;
        // SUBSCRIBE 200 OK, RFC 6665) — it must survive response parsing.
        "In-Reply-To",
        "Max-Forwards",
        "Organization",
        "Priority",
        "Proxy-Authorization",
        "Proxy-Require",
        "Reply-To",
        "Route",
        "Subject"
    };

    private static readonly HashSet<string> ResponseOnlyHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authentication-Info",
        "Error-Info",
        "Min-Expires",
        "Proxy-Authenticate",
        "Retry-After",
        "Server",
        "Unsupported",
        "Warning",
        "WWW-Authenticate"
    };

    /// <summary>
    /// Returns true when duplicate header rows should be comma-combined per RFC3261 section 7.3.
    /// </summary>
    public static bool ShouldCombineRows(string headerName, string existingValue, string nextValue)
    {
        if (string.IsNullOrWhiteSpace(headerName))
            return false;
        if (NonCombinableHeaderNames.Contains(headerName))
            return false;
        if (!CommaCombinableHeaderNames.Contains(headerName))
            return false;

        // Contact allows comma lists except when the value is "*".
        if (headerName.Equals("Contact", StringComparison.OrdinalIgnoreCase)
            && (IsContactWildcard(existingValue) || IsContactWildcard(nextValue)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when header is valid for request processing, or unknown.
    /// </summary>
    public static bool IsApplicableToRequest(string headerName) =>
        !ResponseOnlyHeaderNames.Contains(headerName);

    /// <summary>
    /// Returns true when header is valid for response processing, or unknown.
    /// </summary>
    public static bool IsApplicableToResponse(string headerName) =>
        !RequestOnlyHeaderNames.Contains(headerName);

    /// <summary>
    /// Returns true when header rows with this name must not be comma-combined.
    /// </summary>
    public static bool IsNonCombinable(string headerName) =>
        NonCombinableHeaderNames.Contains(headerName);

    /// <summary>
    /// Returns true when Contact field value is RFC3261 wildcard form.
    /// </summary>
    private static bool IsContactWildcard(string value) =>
        value.Trim().Equals("*", StringComparison.Ordinal);
}
