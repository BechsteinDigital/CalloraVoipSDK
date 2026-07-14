using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Self-signed certificate identifying one endpoint in DTLS-SRTP (RFC 5763 §5):
/// an ephemeral ECDSA P-256 key pair with a SHA-256-signed X.509 wrapper. WebRTC-style
/// media security does not use PKI trust — the certificate is authenticated solely by
/// matching its fingerprint against the peer's signaled SDP <c>a=fingerprint</c> value,
/// so validity period and subject are informational only.
/// </summary>
internal sealed class DtlsCertificate
{
    private readonly AsymmetricKeyParameter _privateKey;
    private readonly X509Certificate _certificate;

    private DtlsCertificate(AsymmetricKeyParameter privateKey, X509Certificate certificate)
    {
        _privateKey = privateKey;
        _certificate = certificate;
        Fingerprint = DtlsFingerprint.FromDerCertificate(certificate.GetEncoded());
    }

    /// <summary>SHA-256 fingerprint for the local SDP <c>a=fingerprint</c> attribute (RFC 8122).</summary>
    public DtlsFingerprint Fingerprint { get; }

    /// <summary>
    /// Generates a fresh ephemeral ECDSA P-256 certificate. Callers should generate one
    /// per endpoint (or per session) and never persist it — rotating identities per
    /// session is the WebRTC privacy default.
    /// </summary>
    public static DtlsCertificate GenerateEcdsaP256()
    {
        var random = new SecureRandom();

        var keyGenerator = new ECKeyPairGenerator();
        keyGenerator.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, random));
        var keyPair = keyGenerator.GenerateKeyPair();

        var name = new X509Name("CN=callora-voip-sdk");
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new BigInteger(63, random).Add(BigInteger.One));
        generator.SetIssuerDN(name);
        generator.SetSubjectDN(name);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        generator.SetNotAfter(DateTime.UtcNow.AddDays(30));
        generator.SetPublicKey(keyPair.Public);

        var certificate = generator.Generate(
            new Asn1SignatureFactory("SHA256WITHECDSA", keyPair.Private, random));

        return new DtlsCertificate(keyPair.Private, certificate);
    }

    /// <summary>
    /// Wraps the certificate as the single-entry TLS certificate chain sent in the
    /// DTLS handshake.
    /// </summary>
    internal Certificate ToTlsCertificate(TlsCrypto crypto)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        var bcCrypto = (BcTlsCrypto)crypto;
        return new Certificate(
            new TlsCertificate[] { new BcTlsCertificate(bcCrypto, _certificate.CertificateStructure) });
    }

    /// <summary>
    /// Builds ECDSA/SHA-256 signer credentials over this certificate for the handshake's
    /// CertificateVerify / ServerKeyExchange signatures.
    /// </summary>
    internal TlsCredentialedSigner CreateSignerCredentials(TlsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(context),
            (BcTlsCrypto)context.Crypto,
            _privateKey,
            ToTlsCertificate(context.Crypto),
            new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }
}
