using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.Security;

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
    /// Context-specific tag <c>[2]</c> for the <c>dNSName</c> GeneralName choice
    /// (RFC 5280 §4.2.1.6). The underlying value is an IMPLICITLY tagged IA5String.
    /// </summary>
    private static readonly Asn1Tag DnsNameTag = new(TagClass.ContextSpecific, 2);

    /// <summary>
    /// Context-specific tag <c>[6]</c> for the <c>uniformResourceIdentifier</c>
    /// GeneralName choice (RFC 5280 §4.2.1.6). The underlying value is an
    /// IMPLICITLY tagged IA5String.
    /// </summary>
    private static readonly Asn1Tag UniformResourceIdentifierTag = new(TagClass.ContextSpecific, 6);

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

        foreach (var entry in ExtractSanEntries(sanExtension))
        {
            var matched = entry.IsDnsName
                ? MatchesDnsNameSan(entry.Value, normalizedDomain)
                : MatchesSipUriSan(entry.Value, normalizedDomain);
            if (matched)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts all SAN entries from the provided certificate extension.
    /// </summary>
    /// <param name="certificate">The certificate to inspect.</param>
    /// <returns>
    /// A read-only list of the actual <c>dNSName</c> and
    /// <c>uniformResourceIdentifier</c> SAN values decoded from the ASN.1 structure;
    /// empty if the extension is absent or malformed.
    /// </returns>
    public static IReadOnlyList<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var sanExtension = certificate.Extensions[SubjectAlternativeNameOid];
        if (sanExtension is null)
            return [];

        var entries = ExtractSanEntries(sanExtension);
        if (entries.Count == 0)
            return [];

        var values = new List<string>(entries.Count);
        foreach (var entry in entries)
            values.Add(entry.Value);

        return values;
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes the raw SAN extension bytes into typed entries by walking the
    /// ASN.1 <c>SEQUENCE OF GeneralName</c> structure (RFC 5280 §4.2.1.6) with
    /// <see cref="AsnReader"/>.
    /// <para>
    /// Only the two GeneralName choices relevant to SIP domain identity are
    /// captured: <c>dNSName</c> (<c>[2] IA5String</c>) and
    /// <c>uniformResourceIdentifier</c> (<c>[6] IA5String</c>). All other choices
    /// (<c>rfc822Name</c>, <c>iPAddress</c>, <c>otherName</c>, …) are skipped by
    /// advancing the reader past their encoded value.
    /// </para>
    /// <para>
    /// This replaces the previous, brittle approach of scraping the culture- and
    /// platform-dependent <see cref="X509Extension.Format(bool)"/> display string.
    /// </para>
    /// </summary>
    /// <param name="sanExtension">The Subject Alternative Name extension to decode.</param>
    /// <returns>
    /// The decoded DNS and URI SAN entries; an empty list if the encoded structure
    /// is malformed.
    /// </returns>
    private static IReadOnlyList<(bool IsDnsName, string Value)> ExtractSanEntries(X509Extension sanExtension)
    {
        var entries = new List<(bool IsDnsName, string Value)>();

        try
        {
            var reader = new AsnReader(sanExtension.RawData, AsnEncodingRules.DER);
            var generalNames = reader.ReadSequence();

            while (generalNames.HasData)
            {
                var tag = generalNames.PeekTag();

                if (tag.HasSameClassAndValue(DnsNameTag))
                {
                    var dnsName = generalNames.ReadCharacterString(UniversalTagNumber.IA5String, DnsNameTag);
                    entries.Add((true, dnsName));
                }
                else if (tag.HasSameClassAndValue(UniformResourceIdentifierTag))
                {
                    var uri = generalNames.ReadCharacterString(UniversalTagNumber.IA5String, UniformResourceIdentifierTag);
                    entries.Add((false, uri));
                }
                else
                {
                    // Skip every other GeneralName choice (rfc822Name [1], iPAddress [7],
                    // otherName [0], directoryName [4], …) by consuming its encoded value
                    // so the reader stays aligned on the next GeneralName.
                    generalNames.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            // The SAN extension is not well-formed DER. Treat it as containing no
            // usable identities rather than throwing or asserting a match.
            return [];
        }

        return entries;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the decoded <c>uniformResourceIdentifier</c>
    /// SAN value is a <c>sip:</c> or <c>sips:</c> URI whose host component matches
    /// <paramref name="normalizedDomain"/>.
    /// </summary>
    private static bool MatchesSipUriSan(string sipUri, string normalizedDomain)
    {
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
    /// Returns <see langword="true"/> if the decoded <c>dNSName</c> SAN value
    /// matches <paramref name="normalizedDomain"/>, including wildcard matching
    /// for the leftmost label per RFC 2818 §3.1.
    /// </summary>
    private static bool MatchesDnsNameSan(string dnsValue, string normalizedDomain)
    {
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
    /// Normalizes a domain string to lowercase and strips trailing dots.
    /// </summary>
    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
