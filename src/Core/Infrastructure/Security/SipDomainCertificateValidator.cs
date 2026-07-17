using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.Infrastructure.Security;

/// <summary>
/// Validates X.509 certificates against SIP domain identities per RFC 5922
/// "Domain Certificates in SIP".
/// <para>
/// RFC 5922 §7.1 requires that when a SIP entity establishes a TLS connection,
/// it MUST verify the server certificate contains a subjectAltName (SAN) extension
/// with a value that matches the expected SIP domain. Two SAN entry types are valid:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <c>uniformResourceIdentifier</c> — a <c>sip:</c> or <c>sips:</c> URI whose
///       host component matches the SIP domain (case-insensitive DNS comparison).
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>dNSName</c> — a DNS hostname that matches the SIP domain, with wildcard
///       support for the leftmost label (e.g. <c>*.example.com</c>).
///     </description>
///   </item>
/// </list>
/// <para>
/// This validator is intentionally stateless and purely functional so that it
/// can be used from callback contexts (e.g. <see cref="System.Net.Security.SslStream"/>
/// certificate validation callbacks) without thread-safety concerns.
/// </para>
/// </summary>
public static class SipDomainCertificateValidator
{
    /// <summary>
    /// OID for the Subject Alternative Name X.509 extension (RFC 5280 §4.2.1.6).
    /// </summary>
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>
    /// Validates that the provided certificate is appropriate for the given SIP domain
    /// per RFC 5922 §7.1.
    /// </summary>
    /// <param name="certificate">
    /// The X.509 certificate to validate. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="sipDomain">
    /// The SIP domain to match against (e.g. <c>example.com</c>, <c>sip.example.com</c>).
    /// Must not be null or whitespace.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the certificate contains a SAN entry that matches
    /// <paramref name="sipDomain"/>; <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// Per RFC 5922 §7.1: "A SIP implementation MUST check the subjectAltName
    /// extension first; if the extension is present and contains the appropriate
    /// SIP domain identity, the check succeeds."
    /// </remarks>
    public static bool ValidateSipDomain(X509Certificate2 certificate, string sipDomain)
    {
        if (string.IsNullOrWhiteSpace(sipDomain))
            return false;

        var normalizedDomain = NormalizeDomain(sipDomain);
        if (string.IsNullOrEmpty(normalizedDomain))
            return false;

        var sanExtension = certificate.Extensions[SubjectAlternativeNameOid];
        if (sanExtension is null)
            return false;

        var sanEntries = ParseSubjectAlternativeNames(sanExtension);
        foreach (var entry in sanEntries)
        {
            if (MatchesSipUriSan(entry, normalizedDomain))
                return true;
            if (MatchesDnsNameSan(entry, normalizedDomain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts all SAN entries from the provided certificate extension.
    /// </summary>
    /// <param name="certificate">The certificate to inspect.</param>
    /// <returns>
    /// A read-only list of SAN string values; empty if the extension is absent.
    /// </returns>
    public static IReadOnlyList<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var sanExtension = certificate.Extensions[SubjectAlternativeNameOid];
        return sanExtension is null
            ? []
            : ParseSubjectAlternativeNames(sanExtension);
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the raw SAN extension bytes into a list of string entries.
    /// <para>
    /// On Linux/.NET, <see cref="X509Extension.Format"/> returns a comma-separated
    /// single-line string of the form: <c>DNS:example.com, URI:sip:proxy@example.com</c>.
    /// On Windows, the format may use newlines and different prefix styles.
    /// This method normalises both variants into a flat list of trimmed tokens.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ParseSubjectAlternativeNames(X509Extension sanExtension)
    {
        var formatted = sanExtension.Format(multiLine: true);
        if (string.IsNullOrWhiteSpace(formatted))
            return [];

        var entries = new List<string>();

        // Split on commas (Linux) and newlines (Windows) to cover both platforms.
        foreach (var segment in formatted.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                entries.Add(trimmed);
        }

        return entries;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the formatted SAN entry represents a
    /// <c>uniformResourceIdentifier</c> SAN with a <c>sip:</c> or <c>sips:</c>
    /// URI whose host component matches <paramref name="normalizedDomain"/>.
    /// </summary>
    private static bool MatchesSipUriSan(string sanEntry, string normalizedDomain)
    {
        // Linux/.NET format:  "URI:sip:proxy@example.com"
        // Windows format:     "URL=sip:proxy@example.com" or "uniformResourceIdentifier=sip:..."
        var sipUri = ExtractSanValue(sanEntry, "uri:", "url=", "uri=", "uniformresourceidentifier=");
        if (sipUri is null)
            return false;

        // Accept sip: and sips: schemes per RFC 5922 §7.1
        if (!sipUri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) &&
            !sipUri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return false;

        // Extract host from URI: after sip: or sips:, the user@host or just host
        var hostStart = sipUri.IndexOf(':', StringComparison.Ordinal) + 1;
        var hostPart = sipUri[hostStart..];

        // Strip userinfo if present (user@host → host)
        var atIndex = hostPart.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            hostPart = hostPart[(atIndex + 1)..];

        // Strip port if present (host:port → host)
        var portIndex = hostPart.IndexOf(':', StringComparison.Ordinal);
        if (portIndex >= 0)
            hostPart = hostPart[..portIndex];

        // Strip any trailing parameters (host;transport=tcp → host)
        var paramIndex = hostPart.IndexOf(';', StringComparison.Ordinal);
        if (paramIndex >= 0)
            hostPart = hostPart[..paramIndex];

        return NormalizeDomain(hostPart) == normalizedDomain;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the formatted SAN entry represents a
    /// <c>dNSName</c> SAN that matches <paramref name="normalizedDomain"/>,
    /// including wildcard matching for the leftmost label per RFC 2818 §3.1.
    /// </summary>
    private static bool MatchesDnsNameSan(string sanEntry, string normalizedDomain)
    {
        // Linux/.NET format:  "DNS:example.com"
        // Windows format:     "DNS Name=example.com" or "dNSName=example.com"
        var dnsValue = ExtractSanValue(sanEntry, "dns:", "dns name=", "dns=", "dnsname=");
        if (dnsValue is null)
            return false;

        var normalizedSan = NormalizeDomain(dnsValue);
        if (string.IsNullOrEmpty(normalizedSan))
            return false;

        // Exact match
        if (normalizedSan == normalizedDomain)
            return true;

        // Wildcard match: *.example.com matches sub.example.com but NOT example.com itself
        if (normalizedSan.StartsWith("*.", StringComparison.Ordinal))
        {
            var wildBase = normalizedSan[2..]; // strip leading "*."
            var dotIndex = normalizedDomain.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex > 0)
            {
                var domainBase = normalizedDomain[(dotIndex + 1)..];
                return domainBase == wildBase;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to extract the value part of a SAN entry by stripping one of the
    /// provided case-insensitive prefixes.
    /// </summary>
    private static string? ExtractSanValue(string sanEntry, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (sanEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return sanEntry[prefix.Length..].Trim();
        }

        return null;
    }

    /// <summary>
    /// Normalizes a domain string to lowercase and strips trailing dots.
    /// </summary>
    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
