namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Thrown by <see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/> when the peer connection fails to
/// establish — the connection reached <see cref="PeerConnectionState.Failed"/> or was closed during
/// negotiation (ICE or DTLS-SRTP did not complete).
/// </summary>
public sealed class WebRtcConnectException : Exception
{
    /// <summary>Creates the exception with a message describing the failure.</summary>
    public WebRtcConnectException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public WebRtcConnectException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
