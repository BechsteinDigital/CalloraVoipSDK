using System.Formats.Asn1;
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
/// The SAN extension is decoded from its ASN.1 (DER) bytes rather than from the
/// locale- and platform-dependent text of <see cref="X509Extension.Format"/>, so the
/// result is identical on every OS and culture. This validator is intentionally
/// stateless and purely functional so it can be used from callback contexts (e.g.
/// <see cref="System.Net.Security.SslStream"/> validation callbacks) without
/// thread-safety concerns.
/// </para>
/// </summary>
internal static class SipDomainCertificateValidator
{
    /// <summary>
    /// OID for the Subject Alternative Name X.509 extension (RFC 5280 §4.2.1.6).
    /// </summary>
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    // GeneralName CHOICE context-specific tags (RFC 5280 §4.2.1.6): dNSName [2] and
    // uniformResourceIdentifier [6], both IA5String with implicit tagging.
    private static readonly Asn1Tag DnsNameTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag UriNameTag = new(TagClass.ContextSpecific, 6);

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

        var (dnsNames, uris) = DecodeSubjectAlternativeNames(sanExtension.RawData);

        foreach (var uri in uris)
        {
            if (MatchesSipUri(uri, normalizedDomain))
                return true;
        }

        foreach (var dnsName in dnsNames)
        {
            if (MatchesDnsName(dnsName, normalizedDomain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the RFC 5922-relevant SAN entries (<c>dNSName</c> and
    /// <c>uniformResourceIdentifier</c> values) from the certificate.
    /// </summary>
    /// <param name="certificate">The certificate to inspect.</param>
    /// <returns>
    /// A read-only list of SAN string values (DNS names followed by URIs); empty if
    /// the extension is absent or malformed.
    /// </returns>
    public static IReadOnlyList<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var sanExtension = certificate.Extensions[SubjectAlternativeNameOid];
        if (sanExtension is null)
            return [];

        var (dnsNames, uris) = DecodeSubjectAlternativeNames(sanExtension.RawData);
        var all = new List<string>(dnsNames.Count + uris.Count);
        all.AddRange(dnsNames);
        all.AddRange(uris);
        return all;
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes the SAN extension value (<c>GeneralNames ::= SEQUENCE OF GeneralName</c>, RFC 5280
    /// §4.2.1.6) from its DER bytes and returns the <c>dNSName</c> and
    /// <c>uniformResourceIdentifier</c> entries. Uses ASN.1 decoding rather than the
    /// locale-/platform-dependent <see cref="X509Extension.Format"/> text. A malformed extension
    /// yields no names (validation then fails closed).
    /// </summary>
    private static (List<string> DnsNames, List<string> Uris) DecodeSubjectAlternativeNames(byte[] rawExtension)
    {
        var dnsNames = new List<string>();
        var uris = new List<string>();

        try
        {
            var generalNames = new AsnReader(rawExtension, AsnEncodingRules.DER).ReadSequence();
            while (generalNames.HasData)
            {
                var tag = generalNames.PeekTag();
                if (tag.HasSameClassAndValue(DnsNameTag))
                    dnsNames.Add(generalNames.ReadCharacterString(UniversalTagNumber.IA5String, DnsNameTag));
                else if (tag.HasSameClassAndValue(UriNameTag))
                    uris.Add(generalNames.ReadCharacterString(UniversalTagNumber.IA5String, UriNameTag));
                else
                    generalNames.ReadEncodedValue(); // skip otherName/rfc822Name/iPAddress/directoryName/…
            }

            return (dnsNames, uris);
        }
        catch (AsnContentException)
        {
            // A malformed SAN extension carries no trustworthy names — fail closed with an empty result
            // rather than a partial parse that a matcher could act on.
            return ([], []);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="uri"/> is a <c>sip:</c>/<c>sips:</c> URI
    /// whose host component matches <paramref name="normalizedDomain"/> (RFC 5922 §7.1).
    /// </summary>
    private static bool MatchesSipUri(string uri, string normalizedDomain)
    {
        // uri is the raw uniformResourceIdentifier value, e.g. "sip:proxy@example.com".
        if (!uri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return false;

        var hostStart = uri.IndexOf(':', StringComparison.Ordinal) + 1;
        var hostPart = uri[hostStart..];

        // Strip userinfo (user@host → host), port (host:port → host) and parameters (host;transport → host).
        var atIndex = hostPart.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            hostPart = hostPart[(atIndex + 1)..];

        var portIndex = hostPart.IndexOf(':', StringComparison.Ordinal);
        if (portIndex >= 0)
            hostPart = hostPart[..portIndex];

        var paramIndex = hostPart.IndexOf(';', StringComparison.Ordinal);
        if (paramIndex >= 0)
            hostPart = hostPart[..paramIndex];

        return NormalizeDomain(hostPart) == normalizedDomain;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="dnsName"/> matches
    /// <paramref name="normalizedDomain"/>, including leftmost-label wildcards per RFC 2818 §3.1.
    /// </summary>
    private static bool MatchesDnsName(string dnsName, string normalizedDomain)
    {
        var normalizedSan = NormalizeDomain(dnsName);
        if (string.IsNullOrEmpty(normalizedSan))
            return false;

        // Exact match.
        if (normalizedSan == normalizedDomain)
            return true;

        // Wildcard match: *.example.com matches sub.example.com but NOT example.com itself.
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
