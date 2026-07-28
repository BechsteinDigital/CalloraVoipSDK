using System.Net;
using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Resolves host and port pairs into concrete remote IP endpoints.
/// Prefers IPv4 to align with common VoIP transport expectations.
/// </summary>
internal static class RemoteEndPointResolver
{
    /// <summary>
    /// Resolves one host/port target into an endpoint.
    /// </summary>
    public static async Task<IPEndPoint> ResolveAsync(
        string host,
        int port,
        CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
            return new IPEndPoint(ipAddress, port);

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return SelectEndPoint(host, addresses, port);
    }

    /// <summary>
    /// Selects the preferred endpoint from a set of resolved addresses, favouring IPv4.
    /// Throws a host-qualified error when the resolution produced no addresses.
    /// </summary>
    internal static IPEndPoint SelectEndPoint(string host, IReadOnlyList<IPAddress> addresses, int port)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException($"DNS resolution for '{host}' returned no addresses.");

        var selected = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addresses[0];
        return new IPEndPoint(selected, port);
    }
}
