namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Thrown when an SRTP packet fails authentication tag verification (RFC 3711 §3.3).
/// The packet must be discarded without decryption.
/// </summary>
internal sealed class SrtpAuthenticationException : Exception
{
    public SrtpAuthenticationException(string message) : base(message) { }

    /// <summary>Wraps the underlying cause — e.g. the AEAD-GCM <c>AuthenticationTagMismatchException</c>.</summary>
    public SrtpAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}
