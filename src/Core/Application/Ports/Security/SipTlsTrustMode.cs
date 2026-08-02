namespace CalloraVoipSdk.Core.Application.Ports.Security;

/// <summary>
/// Trust policy applied to a remote peer's certificate on a SIP TLS/WSS connection.
/// <para>
/// The mode governs only the standard chain/hostname trust decision. It never disables the
/// RFC 5922 <see cref="TlsConfiguration.ExpectedSipDomain"/> identity check nor the rejection of a
/// missing peer certificate — both remain fail-closed in every mode.
/// </para>
/// </summary>
public enum SipTlsTrustMode
{
    /// <summary>
    /// Use the platform trust store: the certificate chain and hostname must validate. This is the
    /// secure default.
    /// </summary>
    System = 0,

    /// <summary>
    /// Accept any certificate chain/hostname result, including self-signed or otherwise untrusted
    /// certificates. <b>Dangerous</b> — intended only for development or controlled test
    /// environments. A configured <see cref="TlsConfiguration.ExpectedSipDomain"/> is still enforced
    /// and a missing certificate is still rejected.
    /// </summary>
    DangerousAcceptAnyChain = 1,
}
