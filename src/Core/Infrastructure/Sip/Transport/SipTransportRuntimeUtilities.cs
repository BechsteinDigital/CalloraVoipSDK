using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

internal static class SipTransportRuntimeUtilities
{
    /// <summary>
    /// Derives the transport a Request-URI demands, per RFC 3261 §26.2.2 and §19.1.5: a <c>sips:</c> scheme
    /// requires TLS (or WSS when the URI names it), and an explicit <c>;transport=</c> parameter selects
    /// directly. Returns <see langword="false"/> when the URI settles nothing, leaving the caller to fall back
    /// to a learned hint or its default.
    /// </summary>
    /// <remarks>
    /// Split out of the runtime because it is the part with a right answer independent of any connection
    /// state: <c>sips:</c> must never come out as plaintext, and that is worth pinning on its own (#336).
    /// </remarks>
    public static bool TryInferTransportFromUri(string? requestUri, out SipTransportProtocol transport)
    {
        transport = SipTransportProtocol.Udp;
        if (string.IsNullOrWhiteSpace(requestUri))
            return false;

        // RFC 3261 §26.2.2: the secure scheme decides before any transport parameter is considered, so a
        // sips: URI can never be talked down to a plaintext transport by its own parameters.
        if (requestUri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            transport = requestUri.Contains(";transport=wss", StringComparison.OrdinalIgnoreCase)
                ? SipTransportProtocol.Wss
                : SipTransportProtocol.Tls;
            return true;
        }

        // Longest token first: ";transport=ws" is a prefix of ";transport=wss".
        if (requestUri.Contains(";transport=wss", StringComparison.OrdinalIgnoreCase))
            transport = SipTransportProtocol.Wss;
        else if (requestUri.Contains(";transport=ws", StringComparison.OrdinalIgnoreCase))
            transport = SipTransportProtocol.Ws;
        else if (requestUri.Contains(";transport=tls", StringComparison.OrdinalIgnoreCase))
            transport = SipTransportProtocol.Tls;
        else if (requestUri.Contains(";transport=tcp", StringComparison.OrdinalIgnoreCase))
            transport = SipTransportProtocol.Tcp;
        else if (requestUri.Contains(";transport=udp", StringComparison.OrdinalIgnoreCase))
            transport = SipTransportProtocol.Udp;
        else
            return false;

        return true;
    }

    public static int AllocateEphemeralPort()
    {
        // Probe on IPAddress.Any so the free-port hint matches the system-wide "+:" scope the WebSocket listener
        // binds on (a loopback-only probe can report a port that is taken on another interface). This is only a
        // best-effort hint — the caller must still handle a bind race by retrying on a fresh port.
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static Uri BuildWebSocketTargetUri(string host, int port, SipTransportProtocol transport)
    {
        // For WSS the host should be the SIP domain so the ClientWebSocket TLS handshake presents
        // the correct SNI and validates the certificate against the domain, not the resolved IP.
        var scheme = transport == SipTransportProtocol.Wss ? "wss" : "ws";
        var builder = new UriBuilder(scheme, host, port, "/");
        return builder.Uri;
    }

    public static IPEndPoint NormalizeWildcardEndPoint(IPEndPoint endpoint)
    {
        if (IPAddress.Any.Equals(endpoint.Address))
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);

        if (IPAddress.IPv6Any.Equals(endpoint.Address))
            return new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port);

        return endpoint;
    }

    public static string BuildEndpointKey(SipTransportProtocol? transport, IPEndPoint endpoint)
    {
        return transport is null
            ? $"{endpoint.Address}:{endpoint.Port}"
            : $"{transport}:{endpoint.Address}:{endpoint.Port}";
    }

    public static IReadOnlyDictionary<string, string> EscalateViaTransportToTcp(
        IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Via", out var via))
            return headers;

        var updatedVia = System.Text.RegularExpressions.Regex.Replace(
            via,
            @"SIP/2\.0/UDP(\s)",
            "SIP/2.0/TCP$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (string.Equals(updatedVia, via, StringComparison.Ordinal))
            return headers;

        var copy = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = updatedVia
        };
        return copy;
    }

    /// <summary>
    /// Picks the TLS target host for an outbound stream connection: the SIP domain recorded for the
    /// endpoint during route resolution (used for SNI and certificate name validation), falling back
    /// to the literal IP address only when no host was resolved (e.g. a call placed directly to an IP).
    /// </summary>
    public static string SelectTlsTargetHost(
        IReadOnlyDictionary<string, string> endpointTlsHosts,
        string endpointKey,
        IPAddress fallbackAddress)
    {
        return endpointTlsHosts.TryGetValue(endpointKey, out var host) && !string.IsNullOrWhiteSpace(host)
            ? host
            : fallbackAddress.ToString();
    }

    /// <summary>
    /// Authenticates an outbound TLS client stream, using <paramref name="targetHost"/> for SNI and
    /// certificate name validation (the SIP domain, not the resolved IP address). When
    /// <paramref name="clientCertificate"/> is supplied it is offered as the client identity for
    /// mutual TLS (RFC 5922 / RFC 8446 §4.4.2).
    /// </summary>
    public static async Task<SslStream> AuthenticateOutboundTlsAsync(
        Stream innerStream,
        string targetHost,
        RemoteCertificateValidationCallback validateCertificate,
        CancellationToken ct,
        X509Certificate2? clientCertificate = null)
    {
        var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, validateCertificate);
        var authOptions = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
        // Mutual TLS: present the SDK's identity certificate as the client certificate. TLS transmits
        // it only when the server sends a CertificateRequest, so registrars that do not request a
        // client certificate see unchanged behaviour (RFC 8446 §4.4.2; issue #183).
        if (clientCertificate is not null)
            authOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        await sslStream.AuthenticateAsClientAsync(authOptions, ct).ConfigureAwait(false);
        return sslStream;
    }

    /// <summary>
    /// Returns "sip" when the WebSocket upgrade request offers the SIP subprotocol (RFC 7118),
    /// otherwise null. RFC 6455 requires the server to echo only a subprotocol the client offered.
    /// </summary>
    public static string? SelectOfferedSipSubProtocol(HttpListenerRequest request)
    {
        var offered = request.Headers["Sec-WebSocket-Protocol"];
        return offered is not null
            && Array.Exists(offered.Split(','), p => p.Trim().Equals("sip", StringComparison.OrdinalIgnoreCase))
                ? "sip"
                : null;
    }
}
