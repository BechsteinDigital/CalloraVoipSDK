using System.Net;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// A signalling-neutral WebRTC peer connection: the app owns the signalling channel (WebSocket/HTTP/
/// Callora) and exchanges SDP through it, while this peer runs ICE, DTLS-SRTP, BUNDLE and the RTP/RTCP
/// transport. Encoded media flows through <see cref="SendAudioAsync"/>/<see cref="SendVideoFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
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

    /// <summary>
    /// Raised as each local ICE candidate is gathered (RFC 8838 trickle), carrying the RFC 8829
    /// <c>candidate:</c> line so the app can signal it to the peer out-of-band. Pair with
    /// <see cref="AddIceCandidateAsync"/> on the remote side. Today one host candidate is gathered.
    /// </summary>
    event EventHandler<string>? LocalIceCandidateDiscovered;

    /// <summary>Produces a local WebRTC offer (BUNDLE, DTLS-SRTP, ICE, rtcp-mux) for the app to signal out.</summary>
    string CreateOffer();

    /// <summary>
    /// Applies a remote ICE candidate that trickled in out-of-band (RFC 8838), as an RFC 8829
    /// <c>candidate:</c> line. The highest-priority component-1 UDP candidate becomes the send target;
    /// a malformed or unusable candidate is ignored.
    /// </summary>
    Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Packetises and sends one already-encoded video frame on a simulcast <paramref name="rid"/> layer
    /// (RFC 8853). The layer must be one of the configured simulcast rids; the app encodes each layer at
    /// its own resolution/bitrate and calls this once per layer per frame.
    /// </summary>
    Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches an <see cref="IMediaTap"/> that observes the encoded media flowing through this peer in both
    /// directions (L3 recording/analytics/AI seam). Dispose the returned handle to detach.
    /// </summary>
    IDisposable AttachMediaTap(IMediaTap tap);

    /// <summary>
    /// Takes a statistics snapshot for this peer (the SDK's <c>getStats</c>). Bitrates are derived per call,
    /// so poll periodically (e.g. once per second) for meaningful rate values.
    /// </summary>
    WebRtcStats GetStats();
}
