using System.Net;
using System.Net.Sockets;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Network;

/// <summary>
/// Resolves the local endpoint address that should be advertised in protocol headers/bodies.
/// Keeps the bound local port and replaces wildcard bind addresses with the routed local address
/// toward the current remote endpoint.
/// </summary>
internal static class LocalEndPointAdvertisementResolver
{
    /// <summary>
    /// Resolves one advertised local endpoint from a bound local endpoint and remote target.
    /// </summary>
    public static IPEndPoint ResolveAdvertisedLocalEndPoint(
        IPEndPoint boundLocalEndPoint,
        IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(boundLocalEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (!IPAddress.Any.Equals(boundLocalEndPoint.Address)                                                                                                                                
            && !IPAddress.IPv6Any.Equals(boundLocalEndPoint.Address)                                                                                                                         
            && !IPAddress.IsLoopback(boundLocalEndPoint.Address))                                                                                                                            
        {                                                                                                                                                                                    
            return boundLocalEndPoint;                                                                                                                                                       
        }  

        try
        {
            // UDP connect selects the effective outbound interface without sending SIP payload.
            using var probe = new Socket(remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remoteEndPoint);
            if (probe.LocalEndPoint is IPEndPoint discovered
                && !IPAddress.Any.Equals(discovered.Address)
                && !IPAddress.IPv6Any.Equals(discovered.Address))
            {
                return new IPEndPoint(discovered.Address, boundLocalEndPoint.Port);
            }
        }
        catch
        {
            // Best-effort resolver: fall back to the bound endpoint.
        }

        return boundLocalEndPoint;
    }
}
