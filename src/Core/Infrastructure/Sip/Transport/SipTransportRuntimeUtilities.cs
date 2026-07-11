using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

internal static class SipTransportRuntimeUtilities
{
    public static int AllocateEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static Uri BuildWebSocketTargetUri(IPEndPoint remoteEndPoint, SipTransportProtocol transport)
    {
        remoteEndPoint = NormalizeWildcardEndPoint(remoteEndPoint);
        var scheme = transport == SipTransportProtocol.Wss ? "wss" : "ws";
        var builder = new UriBuilder(scheme, remoteEndPoint.Address.ToString(), remoteEndPoint.Port, "/");
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
    /// certificate name validation (the SIP domain, not the resolved IP address).
    /// </summary>
    public static async Task<SslStream> AuthenticateOutboundTlsAsync(
        Stream innerStream,
        string targetHost,
        RemoteCertificateValidationCallback validateCertificate,
        CancellationToken ct)
    {
        var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, validateCertificate);
        await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                ct)
            .ConfigureAwait(false);
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
