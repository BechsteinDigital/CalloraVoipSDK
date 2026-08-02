using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.Infrastructure.Security;

/// <summary>
/// Validates X.509 certificates against SIP domain identities per RFC 5922
/// "Domain Certificates in SIP".
/// <para>
/// RFC 5922 §7.1 requires that when a SIP entity establishes a TLS connection,
/// it MUST verify the server certificate contains a subjectAltName (SAN) extension
/// with a value that matches the expected SIP domain. Identity extraction and comparison
/// follow RFC 5922 §7.2 strictly:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <c>uniformResourceIdentifier</c> — only a <c>sip:</c> URI <b>without</b> userinfo is a
///       SIP domain identity; its host is compared to the expected domain. <c>sips:</c>, other
///       schemes and any URI carrying userinfo (which identifies a user, not a domain) are
///       rejected in full and never salvaged.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>dNSName</c> — compared by exact DNS name only. RFC 5922 §7.2 forbids wildcard/suffix
///       expansion, so a <c>*.example.com</c> label matches no concrete host.
///     </description>
///   </item>
/// </list>
/// <para>
/// When at least one valid <c>sip:</c> URI domain identity is present, <c>dNSName</c> entries are
/// NOT consulted as a fallback (RFC 5922 §7.2 URI precedence). All names are canonicalized to
/// lowercase ASCII A-labels (RFC 5280 / IDNA with STD3 rules) before comparison, so a Unicode
/// U-label configured domain matches an A-label SAN and vice versa.
/// </para>
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

    // IDNA canonicalization for domain comparison (RFC 5280 §7 / RFC 5922 §7.2). STD3 rules reject
    // non-host characters (e.g. wildcards, underscores) so they cannot produce a spurious match.
    // IdnMapping.GetAscii does not mutate instance state, so a shared instance is safe for the
    // concurrent callback contexts this validator runs in.
    private static readonly IdnMapping DomainIdn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    // Hard caps bounding the peer-controlled work of SAN decoding (issue #159 P2; ENGINEERING_RULES K4
    // wire-DoS bounds). Generous for legitimate SIP domain certificates, closed against abuse.
    private const int MaxSanEntries = 100;
    private const int MaxSanValueBytes = 1024;
    private const int MaxTotalSanBytes = 16 * 1024;

    /// <summary>
    /// Validates that the provided certificate is appropriate for the given SIP domain
    /// per RFC 5922 §7.1/§7.2.
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
    /// Per RFC 5922 §7.2: valid <c>sip:</c> URI domain identities take precedence over
    /// <c>dNSName</c> entries — if any such URI identity is present, DNS names are not used as a
    /// fallback, even when none of the URI identities matches.
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

        // Stream the SAN entries (no full lists) with only O(1) match state. RFC 5922 §7.2: a valid
        // sip: URI identity — matching or not — suppresses the dNSName fallback, so a dNSName match is
        // held pending until the scan confirms no URI identity exists.
        var hasSipUriIdentity = false;
        var dnsMatched = false;
        var uriMatched = false;

        var scannedWithinBounds = TryScanSubjectAlternativeNames(sanExtension.RawData, (isUri, value) =>
        {
            if (isUri)
            {
                if (TryExtractSipUriDomainIdentity(value, out var uriHost))
                {
                    hasSipUriIdentity = true;
                    if (uriHost == normalizedDomain)
                    {
                        uriMatched = true;
                        return true; // decisive: a matching URI identity ends the scan
                    }
                }
            }
            else if (!hasSipUriIdentity && !dnsMatched && MatchesDnsName(value, normalizedDomain))
            {
                dnsMatched = true; // keep scanning: a later URI identity would suppress this (§7.2)
            }

            return false;
        });

        // Malformed, trailing-data or over-cap SAN work fails closed — never a partial-parse match.
        if (!scannedWithinBounds)
            return false;

        if (uriMatched)
            return true;

        return !hasSipUriIdentity && dnsMatched;
    }

    /// <summary>
    /// Extracts the RFC 5922-relevant SAN entries (<c>dNSName</c> and
    /// <c>uniformResourceIdentifier</c> values) from the certificate as their raw string values.
    /// This is a diagnostic accessor; it applies no identity filtering and must not be used to
    /// make a trust decision (use <see cref="ValidateSipDomain"/> for that).
    /// </summary>
    /// <param name="certificate">The certificate to inspect.</param>
    /// <returns>
    /// A read-only list of SAN string values in certificate order (bounded by the SAN caps); empty if
    /// the extension is absent, malformed or exceeds the caps.
    /// </returns>
    public static IReadOnlyList<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var sanExtension = certificate.Extensions[SubjectAlternativeNameOid];
        if (sanExtension is null)
            return [];

        var all = new List<string>();
        var scannedWithinBounds = TryScanSubjectAlternativeNames(sanExtension.RawData, (_, value) =>
        {
            all.Add(value);
            return false;
        });

        return scannedWithinBounds ? all : [];
    }

    // ──────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams the <c>dNSName</c> and <c>uniformResourceIdentifier</c> SAN values
    /// (<c>GeneralNames ::= SEQUENCE OF GeneralName</c>, RFC 5280 §4.2.1.6) from the extension DER,
    /// applying hard caps on entry count, per-value size and aggregate size (issue #159 P2, K4 wire-DoS
    /// bounds). <paramref name="onEntry"/> is invoked per accepted value as <c>(isUri, value)</c> and may
    /// return <see langword="true"/> to stop early (a decisive match). Uses ASN.1 decoding rather than the
    /// locale-/platform-dependent <see cref="X509Extension.Format"/> text.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the extension was scanned within bounds (whether or not a match was
    /// found); <see langword="false"/> when it was malformed, carried trailing data after the SEQUENCE, or
    /// exceeded a cap — the caller then fails closed.
    /// </returns>
    private static bool TryScanSubjectAlternativeNames(byte[] rawExtension, Func<bool, string, bool> onEntry)
    {
        var entries = 0;
        var totalBytes = 0;

        try
        {
            var outer = new AsnReader(rawExtension, AsnEncodingRules.DER);
            var generalNames = outer.ReadSequence();

            // RFC 5280 §4.2.1.6: nothing may follow the GeneralNames SEQUENCE.
            if (outer.HasData)
                return false;

            while (generalNames.HasData)
            {
                if (++entries > MaxSanEntries)
                    return false;

                var tag = generalNames.PeekTag();
                var isDns = tag.HasSameClassAndValue(DnsNameTag);
                var isUri = tag.HasSameClassAndValue(UriNameTag);
                if (!isDns && !isUri)
                {
                    generalNames.ReadEncodedValue(); // skip otherName/rfc822Name/iPAddress/directoryName/…
                    continue;
                }

                var valueBytes = generalNames.PeekContentBytes().Length;
                totalBytes += valueBytes;
                if (totalBytes > MaxTotalSanBytes)
                    return false;

                if (valueBytes > MaxSanValueBytes)
                {
                    // A single value too large to be a legitimate domain identity — skip it without
                    // allocating a managed string for it.
                    generalNames.ReadEncodedValue();
                    continue;
                }

                if (onEntry(isUri, generalNames.ReadCharacterString(UniversalTagNumber.IA5String, tag)))
                    return true;
            }

            return true;
        }
        catch (AsnContentException)
        {
            // A malformed SAN extension carries no trustworthy names — fail closed rather than acting on
            // a partial parse.
            return false;
        }
    }

    /// <summary>
    /// Attempts to extract the SIP domain identity from a <c>uniformResourceIdentifier</c> SAN
    /// value per RFC 5922 §7.2. Succeeds only for a <c>sip:</c> URI that carries no userinfo,
    /// returning its normalized host in <paramref name="host"/>. <c>sips:</c>, other schemes and
    /// any URI with userinfo are rejected (returns <see langword="false"/>).
    /// </summary>
    private static bool TryExtractSipUriDomainIdentity(string uri, out string host)
    {
        host = string.Empty;

        // RFC 5922 §7.2: only the "sip" scheme identifies a SIP domain. Reject "sips:" explicitly
        // before the "sip:" prefix test would otherwise accept it.
        if (uri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = uri["sip:".Length..];

        // A SIP URI with userinfo (user@host) identifies a user, not a domain — reject in full.
        if (rest.IndexOf('@', StringComparison.Ordinal) >= 0)
            return false;

        // Isolate the host from any port/parameters/headers.
        string hostPart;
        if (rest.StartsWith('['))
        {
            // Bracketed IPv6 literal — not a domain identity, but parse the bracket cleanly.
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return false;
            hostPart = rest[1..close];
        }
        else
        {
            var cut = rest.IndexOfAny([':', ';', '?']);
            hostPart = cut >= 0 ? rest[..cut] : rest;
        }

        host = NormalizeDomain(hostPart);
        return host.Length > 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="dnsName"/> is an exact match for
    /// <paramref name="normalizedDomain"/> after IDNA canonicalization. RFC 5922 §7.2 forbids
    /// wildcard/suffix expansion, so no <c>*.</c> handling is performed.
    /// </summary>
    private static bool MatchesDnsName(string dnsName, string normalizedDomain)
    {
        var normalizedSan = NormalizeDomain(dnsName);
        return normalizedSan.Length > 0 && normalizedSan == normalizedDomain;
    }

    /// <summary>
    /// Canonicalizes a domain to its lowercase ASCII A-label form for comparison
    /// (RFC 5280 §7 / IDNA with STD3 rules). Returns <see cref="string.Empty"/> for input that is
    /// not a valid host label so the caller fails closed.
    /// </summary>
    private static string NormalizeDomain(string domain)
    {
        var trimmed = domain.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
            return string.Empty;

        try
        {
            // GetAscii applies IDNA ToASCII (case-folding + punycode); the ASCII result is then
            // lower-cased so plain-ASCII labels compare case-insensitively too.
            return DomainIdn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Not a valid host under STD3 rules (e.g. wildcard, illegal characters, empty label) —
            // fail closed with an empty identity rather than acting on a partial value (RFC 5922 §7.2).
            return string.Empty;
        }
    }
}
