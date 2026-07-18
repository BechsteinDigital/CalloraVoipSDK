namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// The L2 registry of peer connections opened through a <see cref="IWebRtcClient"/> — the WebRTC
/// counterpart to the SIP <c>ICallManager</c>. Peers are tracked on creation and removed when disposed, so
/// a multi-peer app (e.g. a conference) can enumerate its live connections without keeping its own list.
/// </summary>
public interface IPeerConnectionManager
{
    /// <summary>A point-in-time snapshot of the peer connections currently open on this client.</summary>
    IReadOnlyList<IPeerConnection> Active { get; }

    /// <summary>The number of peer connections currently open on this client.</summary>
    int Count { get; }
}
