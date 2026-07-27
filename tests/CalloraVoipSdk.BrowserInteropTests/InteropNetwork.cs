using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CalloraVoipSdk.BrowserInteropTests;

/// <summary>
/// Ermittelt die lokale (LAN-)IPv4, auf die die SDK-Fassade in den Browser-Interop-Tests bindet.
/// Anders als Chromium generiert Firefox <b>keine</b> <c>127.0.0.1</c>-ICE-Candidates (auch nicht mit
/// <c>media.peerconnection.ice.loopback</c>) — es bietet nur die echten host-Adressen an. Bindet die
/// Fassade auf Loopback, kann sie den LAN-Candidate des Browsers nicht erreichen und ICE scheitert.
/// Deshalb binden beide Seiten auf dieselbe host-IPv4. Das ist zugleich die realitätsnähere Topologie.
/// </summary>
internal static class InteropNetwork
{
    /// <summary>
    /// Die IPv4 des ausgehenden Interfaces (per Routing-Auswahl ermittelt, ohne Traffic zu senden),
    /// mit Fallback auf die erste aktive nicht-Loopback-IPv4 und zuletzt auf Loopback.
    /// </summary>
    public static IPAddress LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // kein Traffic — wählt nur das ausgehende Interface
            if (socket.LocalEndPoint is IPEndPoint { Address: var routed } && !IPAddress.IsLoopback(routed))
                return routed;
        }
        catch (SocketException)
        {
            // Kein Default-Gateway (z. B. isolierter CI-Runner) → Interface-Enumeration unten.
        }

        // Virtuelle Bridges (docker0, br-*, veth*, virbr*) zuletzt: der Browser generiert seine host-Candidates
        // auf dem physischen Interface, ein Bind auf die Docker-Bridge läge auf einem anderen Subnetz → ICE-Fail.
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up)
                     .OrderBy(n => IsVirtualBridge(n.Name) ? 1 : 0))
        {
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    return addr.Address;
        }
        return IPAddress.Loopback;
    }

    private static bool IsVirtualBridge(string name) =>
        name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase);
}
