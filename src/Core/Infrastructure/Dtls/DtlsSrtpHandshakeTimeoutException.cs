namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Raised when a DTLS-SRTP handshake exceeds its
/// <see cref="DtlsHandshakeOptions.HandshakeTimeout"/> before completing (#163 P1-1).
/// A subtype of <see cref="DtlsSrtpHandshakeException"/> so the media session's fail-closed
/// teardown handles a deadline abort like any other handshake failure, while callers that
/// care can still distinguish a timed-out peer from a protocol failure. Distinct from
/// <see cref="OperationCanceledException"/>, which signals caller-driven session teardown.
/// </summary>
internal sealed class DtlsSrtpHandshakeTimeoutException : DtlsSrtpHandshakeException
{
    public DtlsSrtpHandshakeTimeoutException(string message)
        : base(message)
    {
    }
}
