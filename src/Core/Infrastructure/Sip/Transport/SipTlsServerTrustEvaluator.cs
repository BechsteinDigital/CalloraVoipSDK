using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Application.Ports.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Fail-closed trust decision for a remote SIP TLS/WSS server certificate (RFC 5922 §7.1,
/// issue #159 P1). Kept as a pure, side-effect-free function so the full decision matrix is unit
/// testable independently of a live <see cref="System.Net.Security.SslStream"/> handshake.
/// </summary>
internal static class SipTlsServerTrustEvaluator
{
    /// <summary>
    /// Evaluates whether a peer certificate is acceptable.
    /// </summary>
    /// <param name="trustMode">Configured trust policy for standard chain/hostname validation.</param>
    /// <param name="expectedSipDomain">
    /// The RFC 5922 SIP domain that must be present in the certificate SAN, or <see langword="null"/>/empty
    /// to skip the identity check.
    /// </param>
    /// <param name="certificate">The peer certificate, or <see langword="null"/> if none was presented.</param>
    /// <param name="sslPolicyErrors">Standard TLS validation result from the platform.</param>
    /// <param name="sipDomainMatches">
    /// Delegate that checks the certificate against <paramref name="expectedSipDomain"/> (typically the
    /// certificate provider), or <see langword="null"/> when no provider is available.
    /// </param>
    /// <returns>
    /// A tuple whose <c>Accepted</c> flag is <see langword="true"/> only when the certificate passes
    /// every applicable check; <c>Reason</c> carries a redaction-safe rejection message otherwise.
    /// </returns>
    internal static (bool Accepted, string? Reason) Evaluate(
        SipTlsTrustMode trustMode,
        string? expectedSipDomain,
        X509Certificate? certificate,
        SslPolicyErrors sslPolicyErrors,
        Func<X509Certificate2, bool>? sipDomainMatches)
    {
        // A missing peer certificate is rejected in every mode (RFC 5922 §7.1). This must be checked
        // before the trust mode so DangerousAcceptAnyChain cannot accept an absent certificate.
        if (certificate is null)
            return (false, "no peer certificate presented");

        var expectsSipDomain = !string.IsNullOrWhiteSpace(expectedSipDomain);

        if (trustMode != SipTlsTrustMode.DangerousAcceptAnyChain)
        {
            // Chain, time, usage and revocation failures are terminal regardless of identity.
            var nonNameErrors = sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
            if (nonNameErrors != SslPolicyErrors.None)
                return (false, $"standard TLS validation failed: {sslPolicyErrors}");

            // RFC 5922 §7.3: a pure hostname mismatch (e.g. a URI-only or non-matching-dNSName SIP
            // identity) may be rescued ONLY by the successful strict RFC 5922 domain match below. With
            // no configured domain to compare against, the mismatch stays terminal.
            if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None
                && (!expectsSipDomain || sipDomainMatches is null))
                return (false, $"standard TLS validation failed: {sslPolicyErrors}");
        }

        // RFC 5922 §7.1 SIP-domain identity check — always fail-closed when configured, independent of
        // trust mode.
        if (!expectsSipDomain)
            return (true, null);

        if (sipDomainMatches is null)
            return (false, $"RFC 5922 SIP domain '{expectedSipDomain}' configured but no certificate provider is available");

        // The SslStream callback almost always supplies an X509Certificate2, but the signature only
        // guarantees the base type. Upgrade a base instance to a temporary X509Certificate2 and dispose
        // it deterministically; any conversion failure rejects rather than skipping the check.
        X509Certificate2? converted = null;
        try
        {
            var cert2 = certificate as X509Certificate2;
            if (cert2 is null)
            {
                converted = ImportCertificate2(certificate);
                cert2 = converted;
            }

            return sipDomainMatches(cert2)
                ? (true, null)
                : (false, $"RFC 5922 SIP domain SAN validation failed for '{expectedSipDomain}'");
        }
        catch (CryptographicException ex)
        {
            return (false, $"peer certificate could not be processed for RFC 5922 validation: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // A disposed/keyless certificate handle can surface as InvalidOperationException from
            // Export(); treat any such failure as a rejection rather than letting it escape the callback.
            return (false, $"peer certificate could not be processed for RFC 5922 validation: {ex.Message}");
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static X509Certificate2 ImportCertificate2(X509Certificate certificate)
    {
        var der = certificate.Export(X509ContentType.Cert);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(der);
#else
        return new X509Certificate2(der);
#endif
    }
}
