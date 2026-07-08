using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Formatting utilities for SIP signaling headers.
/// </summary>
internal static class SipSignalingFormat
{
    /// <summary>
    /// Builds a Via header value for local transport endpoint and branch token.
    /// </summary>
    /// <param name="localEndPoint">Bound local endpoint; supplies the fallback host/port.</param>
    /// <param name="branch">RFC 3261 branch token.</param>
    /// <param name="transport">Signaling transport.</param>
    /// <param name="advertisedHost">
    /// Optional public host (IP or FQDN) advertised as sent-by instead of the resolved
    /// local host — required behind NAT. <see langword="null"/> uses the local address.
    /// </param>
    /// <param name="advertisedPort">
    /// Optional public port paired with <paramref name="advertisedHost"/>.
    /// <see langword="null"/> or 0 reuses the local port.
    /// </param>
    public static string BuildVia(
        IPEndPoint localEndPoint,
        string branch,
        SipTransportProtocol transport,
        string? advertisedHost = null,
        int? advertisedPort = null)
    {
        var host = ResolveAdvertisedHost(localEndPoint, advertisedHost);
        var port = ResolveAdvertisedPort(localEndPoint, advertisedPort);
        var transportToken = transport switch
        {
            SipTransportProtocol.Tcp => "TCP",
            SipTransportProtocol.Tls => "TLS",
            SipTransportProtocol.Ws => "WS",
            SipTransportProtocol.Wss => "WSS",
            _ => "UDP"
        };

        return $"SIP/2.0/{transportToken} {host}:{port};branch={branch};rport";
    }

    /// <summary>
    /// Builds a Contact URI for the local endpoint and user.
    /// </summary>
    /// <param name="username">Address-of-record user part.</param>
    /// <param name="localEndPoint">Bound local endpoint; supplies the fallback host/port.</param>
    /// <param name="transport">Signaling transport.</param>
    /// <param name="forceSecureScheme">Forces the <c>sips</c> scheme when true.</param>
    /// <param name="advertisedHost">
    /// Optional public host (IP or FQDN) advertised instead of the resolved local host —
    /// required behind NAT for public trunks. <see langword="null"/> uses the local address.
    /// </param>
    /// <param name="advertisedPort">
    /// Optional public port paired with <paramref name="advertisedHost"/>.
    /// <see langword="null"/> or 0 reuses the local port.
    /// </param>
    public static string BuildContactUri(
        string username,
        IPEndPoint localEndPoint,
        SipTransportProtocol transport,
        bool forceSecureScheme = false,
        string? advertisedHost = null,
        int? advertisedPort = null)
    {
        var host = ResolveAdvertisedHost(localEndPoint, advertisedHost);
        var port = ResolveAdvertisedPort(localEndPoint, advertisedPort);
        var scheme = forceSecureScheme || transport is SipTransportProtocol.Tls or SipTransportProtocol.Wss
            ? "sips"
            : "sip";

        var transportParam = transport switch
        {
            SipTransportProtocol.Tcp => ";transport=tcp",
            SipTransportProtocol.Tls => ";transport=tls",
            SipTransportProtocol.Ws => ";transport=ws",
            SipTransportProtocol.Wss => ";transport=wss",
            _ => ";transport=udp"
        };

        return $"{scheme}:{username}@{host}:{port}{transportParam}";
    }

    private static string ResolveAdvertisedHost(IPEndPoint localEndPoint, string? advertisedHost) =>
        string.IsNullOrWhiteSpace(advertisedHost)
            ? LocalEndPointHostResolver.ResolveHost(localEndPoint)
            : advertisedHost.Trim();

    private static int ResolveAdvertisedPort(IPEndPoint localEndPoint, int? advertisedPort) =>
        advertisedPort is > 0 ? advertisedPort.Value : localEndPoint.Port;
}
