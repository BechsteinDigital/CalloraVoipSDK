using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Resolves a configured TURN server entry into the wire values the relay gathering path needs — its
/// transport address in the media socket's address family and its long-term credentials. Extracted from
/// <see cref="WebRtcPeerConnection"/> as a self-contained collaborator (no shared state) to keep that file
/// under the 1000-line limit (ENGINEERING_RULES R3).
/// </summary>
internal static class WebRtcTurnServerResolver
{
    private const int DefaultTurnPort = 3478;

    /// <summary>
    /// Long-term TURN credentials from the configured username/password, or null for an open server. A
    /// bootstrap realm (the server host, replaced by the server's real realm on the 401 challenge, and never
    /// put on the wire — the first Allocate is unauthenticated) marks the credentials long-term so the
    /// allocation runs the RFC 5389 §10.2 challenge flow. That flow yields the effective realm/nonce the relay
    /// coordinator needs to adopt the allocation without re-challenging; short-term credentials skip it.
    /// </summary>
    public static StunCredentials? BuildCredentials(IceServerConfiguration server)
        => string.IsNullOrWhiteSpace(server.Username) || string.IsNullOrWhiteSpace(server.Password)
            ? null
            : new StunCredentials { Username = server.Username, Password = server.Password, Realm = server.Host };

    /// <summary>
    /// Resolves the TURN server's transport address in the media socket's <paramref name="addressFamily"/>
    /// (RFC 8656 default port 3478), or null when no address in that family resolves — a mismatched family
    /// would fail the send.
    /// </summary>
    public static async Task<IPEndPoint?> ResolveEndPointAsync(
        IceServerConfiguration server, AddressFamily addressFamily, CancellationToken ct)
    {
        var port = server.Port ?? DefaultTurnPort;
        if (IPAddress.TryParse(server.Host, out var ip))
            return new IPEndPoint(ip, port);

        var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
        var address = StunIceProbe.PickAddressForFamily(addresses, addressFamily);
        return address is null ? null : new IPEndPoint(address, port);
    }
}
