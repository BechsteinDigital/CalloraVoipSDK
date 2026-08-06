using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Test helper for the issue #183 TLS handshake tests. <see cref="CertificateRequest.CreateSelfSigned"/>
/// produces a certificate whose private key is ephemeral; Windows SChannel cannot use such a key as a
/// server certificate, so <see cref="System.Net.Security.SslStream.AuthenticateAsServerAsync(System.Net.Security.SslServerAuthenticationOptions,System.Threading.CancellationToken)"/>
/// aborts the handshake ("unexpected EOF"). Round-tripping the certificate through PKCS#12 yields a
/// private key that is usable for TLS server authentication on every platform (this mirrors the
/// production loader's <c>#if NET9_0_OR_GREATER</c> split).
/// </summary>
internal static class Issue183TestCertificates
{
    /// <summary>
    /// Returns a copy of <paramref name="ephemeral"/> whose private key is persisted so it can serve as
    /// an SslStream server certificate on Windows as well as Linux/macOS. The caller owns and disposes
    /// the returned instance.
    /// </summary>
    public static X509Certificate2 WithUsablePrivateKey(X509Certificate2 ephemeral)
    {
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
        return new X509Certificate2(pfx);
#endif
    }
}
