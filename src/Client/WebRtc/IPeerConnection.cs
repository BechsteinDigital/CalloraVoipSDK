using System.Net;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A signalling-neutral WebRTC peer connection: the app owns the signalling channel (WebSocket/HTTP/
/// Callora) and exchanges SDP through it, while this peer runs ICE, DTLS-SRTP, BUNDLE and the RTP/RTCP
/// transport. Encoded media flows through <see cref="SendAudioAsync"/>/<see cref="SendVideoFrameAsync"/>
/// — the SDK is transport-only, so the app owns the codec. The central per-connection abstraction of the
/// WebRTC facade, mirroring the SIP <c>ICall</c>.
/// </summary>
public interface IPeerConnection : IAsyncDisposable
{
    /// <summary>Current lifecycle state (RFC 8829).</summary>
    PeerConnectionState State { get; }

    /// <summary>The local SDP (offer or answer) once one has been produced; <see langword="null"/> before.</summary>
    string? LocalDescription { get; }

    /// <summary>The bound local media endpoint once the transport has bound; <see langword="null"/> before.</summary>
    IPEndPoint? LocalMediaEndPoint { get; }

    /// <summary>Raised on every lifecycle transition (RFC 8829 <c>connectionstatechange</c>).</summary>
    event EventHandler<PeerConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised once per remote track, when that track's first frame arrives (the W3C <c>track</c> event).
    /// Subscribe to the track's <see cref="RemoteTrack.FrameReceived"/> synchronously in the handler to
    /// receive every frame — the first frame is delivered immediately after this event returns.
    /// </summary>
    event EventHandler<RemoteTrack>? TrackReceived;

    /// <summary>Produces a local WebRTC offer (BUNDLE, DTLS-SRTP, ICE, rtcp-mux) for the app to signal out.</summary>
    string CreateOffer();

    /// <summary>
    /// Applies a remote SDP. When this peer is the answerer, returns the local answer SDP to signal back;
    /// when it is the offerer applying the peer's answer, returns the local offer unchanged.
    /// </summary>
    Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default);

    /// <summary>Starts the media transport (ICE connectivity, DTLS handshake, receive loop).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one already-encoded audio RTP payload on the peer's audio track.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Packetises and sends one already-encoded video frame on the peer's video track.</summary>
    Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);
}
