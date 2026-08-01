using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Expands a wildcard bind into actual addresses of active interfaces. Socket ownership remains wildcard-based,
/// while signalling exposes only peer-reachable bases (RFC 8445 §5.1.1.1), matching libwebrtc's separation of
/// port allocation from candidate advertisement.
/// </summary>
internal sealed class SystemWebRtcHostCandidateProvider(ILogger<SystemWebRtcHostCandidateProvider> logger)
    : IWebRtcHostCandidateProvider
{
    /// <inheritdoc />
    public IReadOnlyList<IPEndPoint> GetHostEndPoints(IPEndPoint boundEndPoint)
    {
        ArgumentNullException.ThrowIfNull(boundEndPoint);
        if (WebRtcIceCandidateFactory.CanAdvertiseLocalHost(boundEndPoint))
            return [boundEndPoint];

        var discovered = new List<(IPAddress Address, int Cost)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            logger.LogWarning(ex, "Unable to enumerate interfaces for wildcard-bound WebRTC ICE gathering.");
            return [];
        }

        foreach (var network in interfaces)
        {
            if (network.OperationalStatus != OperationalStatus.Up
                || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == boundEndPoint.AddressFamily && IsUsable(address))
                        discovered.Add((address, NetworkCost(network.NetworkInterfaceType)));
                }
            }
            catch (NetworkInformationException ex)
            {
                logger.LogDebug(ex, "Skipping interface {Interface} while gathering WebRTC host candidates.", network.Name);
            }
        }

        return discovered
            .OrderBy(entry => entry.Cost)
            .ThenBy(entry => entry.Address.ToString(), StringComparer.Ordinal)
            .Select(entry => entry.Address)
            .Distinct()
            .Select(address => new IPEndPoint(address, boundEndPoint.Port))
            .ToArray();
    }

    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily != AddressFamily.InterNetwork || bytes[0] != 169 || bytes[1] != 254;
    }

    private static int NetworkCost(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        NetworkInterfaceType.Ppp => 2,
        _ => 3,
    };
}
