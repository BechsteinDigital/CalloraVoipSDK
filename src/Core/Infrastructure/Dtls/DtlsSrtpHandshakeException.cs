namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Raised when a DTLS-SRTP handshake (RFC 5764) fails — transport errors, TLS alerts,
/// a missing <c>use_srtp</c> extension, or a certificate fingerprint mismatch
/// (RFC 5763 §6.7.1). The media session must be torn down; keys are never usable.
/// </summary>
internal sealed class DtlsSrtpHandshakeException : Exception
{
    public DtlsSrtpHandshakeException(string message)
        : base(message)
    {
    }

    public DtlsSrtpHandshakeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
