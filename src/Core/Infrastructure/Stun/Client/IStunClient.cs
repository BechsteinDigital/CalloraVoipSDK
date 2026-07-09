using System.Net;
using System.Net.Security;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// Performs STUN Binding Requests to discover the public reflexive address of a local socket
/// as observed by a STUN server (RFC 5389 §7).
/// </summary>
internal interface IStunClient
{
    /// <summary>
    /// Sends a Binding Request to <paramref name="serverEndPoint"/> and returns the public endpoint
    /// as observed by the server.
    /// <para>
    /// For UDP: retransmits using the RFC 5389 §7.2.1 doubling-RTO schedule before giving up.
    /// </para>
    /// <para>
    /// When <paramref name="credentials"/> are provided, MESSAGE-INTEGRITY is added to the request.
    /// For short-term credentials the key is SASLprep(password).
    /// For long-term credentials the client first sends the request without a credential, then
    /// handles the 401/438 challenge/response exchange automatically (RFC 5389 §10.2).
    /// </para>
    /// </summary>
    /// <param name="serverEndPoint">STUN server endpoint to query.</param>
    /// <param name="credentials">
    /// Optional credentials for authenticated requests. <c>null</c> means unauthenticated.
    /// </param>
    /// <param name="transport">
    /// STUN transport to use. UDP uses RFC 5389 retransmissions; TCP/TLS use one request/response exchange.
    /// </param>
    /// <param name="tlsTargetHost">
    /// Optional TLS SNI/hostname when <paramref name="transport"/> is <see cref="StunTransport.Tls"/>.
    /// Defaults to the endpoint IP string.
    /// </param>
    /// <param name="tlsRemoteCertificateValidationCallback">
    /// Optional TLS certificate validation callback. When null, platform-default validation is used.
    /// </param>
    /// <param name="localEndPoint">
    /// Optional local endpoint to bind before sending the request.
    /// Mainly used by ICE candidate checks to preserve source-port semantics.
    /// </param>
    /// <param name="sharedUdpSocket">
    /// Optional already-bound UDP socket to send/receive through instead of binding a new
    /// one. Required when the source port is owned by another component (e.g. the RTP
    /// media port during ICE gathering) — binding a second socket to that port fails with
    /// "address already in use". The socket is neither connected nor disposed. UDP only.
    /// </param>
    /// <param name="additionalAttributes">
    /// Optional attributes inserted into the request before MESSAGE-INTEGRITY (and thus covered
    /// by it). Used by ICE connectivity checks to carry PRIORITY and ICE-CONTROLLING /
    /// ICE-CONTROLLED (RFC 8445 §7.2.2). Not forwarded across a 300 Try Alternate redirect.
    /// </param>
    /// <param name="ct">Cancellation token to abort all retry attempts immediately.</param>
    /// <returns>The resolved public endpoint.</returns>
    /// <exception cref="StunException">
    /// Thrown when the server returns an error response or all retransmission attempts are exhausted.
    /// </exception>
    Task<StunBindingResult> QueryBindingAsync(
        IPEndPoint        serverEndPoint,
        StunCredentials?  credentials = null,
        StunTransport     transport = StunTransport.Udp,
        string?           tlsTargetHost = null,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback = null,
        IPEndPoint?       localEndPoint = null,
        System.Net.Sockets.Socket? sharedUdpSocket = null,
        IReadOnlyList<StunAttribute>? additionalAttributes = null,
        CancellationToken ct          = default);
}
