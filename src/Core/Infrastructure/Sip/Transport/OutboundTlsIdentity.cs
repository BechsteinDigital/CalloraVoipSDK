using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Resolved outbound TLS identity for one SIP line (or the client-wide default): the client
/// certificate presented for mutual TLS, the server-certificate trust callback to apply on the
/// handshake, and a pool discriminator so connections with different identities are never shared
/// (issue #183). Mirrors how reference stacks (Asterisk <c>transport=</c>, pjsip per-account
/// transports) give each line its own TLS-identity-bound connection.
/// </summary>
internal sealed class OutboundTlsIdentity
{
    /// <summary>
    /// Creates a resolved outbound TLS identity.
    /// </summary>
    /// <param name="clientCertificate">Client certificate to present for mutual TLS, or <see langword="null"/> to present none.</param>
    /// <param name="validateServerCertificate">Server-certificate validation callback applied on the outbound handshake.</param>
    /// <param name="key">
    /// Connection-pool discriminator. The empty string denotes the client-wide default and keeps the
    /// pool key byte-identical to the pre-#183 behaviour; a non-empty string isolates a per-line identity.
    /// </param>
    public OutboundTlsIdentity(
        X509Certificate2? clientCertificate,
        RemoteCertificateValidationCallback validateServerCertificate,
        string key)
    {
        ClientCertificate = clientCertificate;
        ValidateServerCertificate = validateServerCertificate ?? throw new ArgumentNullException(nameof(validateServerCertificate));
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    /// <summary>
    /// Client certificate to present for mutual TLS, or <see langword="null"/> to present none. The
    /// certificate is caller-owned; the transport never disposes it.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; }

    /// <summary>
    /// Server-certificate validation callback to apply on the outbound handshake for this identity.
    /// </summary>
    public RemoteCertificateValidationCallback ValidateServerCertificate { get; }

    /// <summary>
    /// Connection-pool discriminator. Empty for the client-wide default; a non-empty identity string
    /// (client-certificate thumbprint plus server-trust policy) for a per-line identity so two lines
    /// with different identities to the same endpoint get distinct pooled connections.
    /// </summary>
    public string Key { get; }
}
