namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The single WebRTC-facade rule about ICE-server entries, shared by every door into the configuration
/// (<see cref="WebRtcConfiguration"/>, the options validator and the DI builder) so they cannot drift apart
/// (#166 P2-7). Only UDP TURN gathers a relay candidate here: <see cref="WebRtcClient.CreatePeer"/> builds a
/// TURN allocation probe for UDP only, because the TCP/TLS relay data path is not wired into the WebRTC media
/// bundle yet (that feature is tracked in #155). A TCP/TLS TURN entry is therefore a silent trap — accepted,
/// but producing no relay candidate at all — and is rejected wherever it is supplied.
/// </summary>
internal static class WebRtcIceServerPolicy
{
    /// <summary>Whether <paramref name="server"/> is a TURN entry on a transport the facade cannot relay over.</summary>
    internal static bool IsUnsupportedTurnTransport(IceServerConfiguration server)
        => server.Type == IceServerType.Turn && server.Transport != IceTransport.Udp;

    /// <summary>The one message every door uses for a rejected TURN transport.</summary>
    internal static string UnsupportedTurnTransportMessage(IceServerConfiguration server)
        => $"TURN server '{server.Host}' uses transport '{server.Transport}'; only UDP TURN is supported " +
           "for WebRTC relay gathering. Use IceTransport.Udp.";
}
