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
    /// <see cref="AddIceCandidateAsync"/> on the remote side. The host candidate is surfaced at offer/answer
    /// time; server-reflexive candidates follow from <see cref="GatherCandidatesAsync"/> when STUN servers
    /// are configured.
    /// </summary>
    event EventHandler<string>? LocalIceCandidateDiscovered;

    /// <summary>
    /// Raised once per fully received inbound DTMF tone (RFC 4733 telephone-event). Carries the decoded tone
    /// and duration; DTMF is not surfaced as audio on the remote audio track. Only fires when the negotiation
    /// included telephone-event.
    /// </summary>
    event EventHandler<DtmfTone>? DtmfReceived;

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

    /// <summary>
    /// Gathers server-reflexive ICE candidates (RFC 8445 §5.1.1) from the configured STUN servers, each
    /// surfaced on <see cref="LocalIceCandidateDiscovered"/> to trickle out (RFC 8838). No-op when no STUN
    /// servers are configured. Call after producing the offer/answer and before <see cref="StartAsync"/>
    /// (the query shares the media socket the transport takes over once started; calling it after
    /// <see cref="StartAsync"/> throws). On loopback (the STUN server on the same host) the reflexive
    /// address equals the host candidate; redundant-candidate pruning (RFC 8445 §5.4) is a later slice.
    /// </summary>
    Task GatherCandidatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the media transport (ICE connectivity, DTLS handshake, receive loop).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one already-encoded audio RTP payload on the peer's audio track. A no-op when the negotiated
    /// directions do not carry outbound audio from this peer (a send-only/inactive remote answer, or a
    /// recv-only/inactive local side, RFC 3264): the audio m-line still anchors the transport and inbound
    /// audio is still received, but nothing is streamed to a remote that will not receive it.
    /// </summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Packetises and sends one already-encoded video frame on the peer's video track.</summary>
    Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one out-of-band DTMF tone (RFC 4733 telephone-event) on the peer's audio track. A no-op is not
    /// possible: telephone-event must have been negotiated, otherwise this throws. The tone is streamed as an
    /// event burst on the audio stream's RTP clock, suppressed until the DTLS handshake keys the transport.
    /// </summary>
    /// <param name="toneCode">The DTMF event code (0–9, 10=*, 11=#, 12–15=A–D per RFC 4733 §3.2).</param>
    /// <param name="durationMs">The tone duration in milliseconds (default 160; at least the RFC 4733 floor).</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="ArgumentOutOfRangeException">The tone code exceeds 15, or the duration is below the floor.</exception>
    /// <exception cref="InvalidOperationException">No media session yet, or telephone-event was not negotiated.</exception>
    Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default);

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
