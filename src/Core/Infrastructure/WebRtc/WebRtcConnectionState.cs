namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Lifecycle state of a <see cref="WebRtcPeerConnection"/>, modelled on the W3C
/// <c>RTCPeerConnectionState</c> (RFC 8829): a signalling-neutral view of where the peer is between
/// creation and teardown. ICE and DTLS progress drive the transitions in later slices.
/// </summary>
internal enum WebRtcConnectionState
{
    /// <summary>Created; no remote description applied yet.</summary>
    New = 0,

    /// <summary>A remote description was applied and the transport is being established (ICE/DTLS).</summary>
    Connecting,

    /// <summary>ICE consent and the DTLS handshake are established; media can flow.</summary>
    Connected,

    /// <summary>Connectivity was lost transiently (RFC 7675 consent miss) but may recover.</summary>
    Disconnected,

    /// <summary>Negotiation or the transport failed unrecoverably.</summary>
    Failed,

    /// <summary>The peer was closed and its resources released.</summary>
    Closed,
}
