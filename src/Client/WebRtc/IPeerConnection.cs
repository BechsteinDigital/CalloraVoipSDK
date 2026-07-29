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

    /// <summary>
    /// The current RFC 8829 §4.1.3 signalling state — the offer/answer half of the peer's lifecycle,
    /// distinct from the ICE/DTLS transport <see cref="State"/>. Starts at <see cref="SignalingState.Stable"/>
    /// and settles back there once each offer/answer exchange completes; see <see cref="SignalingState"/> for
    /// the transitions this SDK models. Mirrors the W3C <c>RTCPeerConnection.signalingState</c>.
    /// </summary>
    SignalingState SignalingState { get; }

    /// <summary>The local SDP (offer or answer) once one has been produced; <see langword="null"/> before.</summary>
    string? LocalDescription { get; }

    /// <summary>The bound local media endpoint once the transport has bound; <see langword="null"/> before.</summary>
    IPEndPoint? LocalMediaEndPoint { get; }

    /// <summary>Raised on every lifecycle transition (RFC 8829 <c>connectionstatechange</c>).</summary>
    event EventHandler<PeerConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised on every RFC 8829 §4.1.3 signalling-state transition (the W3C <c>signalingstatechange</c>),
    /// carrying the new <see cref="SignalingState"/>. The answerer path fires twice in one
    /// <see cref="SetRemoteDescriptionAsync"/> call — once for <see cref="SignalingState.HaveRemoteOffer"/>
    /// and once for the return to <see cref="SignalingState.Stable"/>.
    /// </summary>
    event EventHandler<SignalingState>? SignalingStateChanged;

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

    /// <summary>
    /// Raised when the remote peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104). This targets
    /// the local encoder — the app should encode and send a key frame so the peer can recover its video.
    /// </summary>
    event EventHandler? VideoKeyFrameRequested;

    /// <summary>
    /// Adds a video track (its own <c>m=video</c> line on the shared BUNDLE transport), returning a handle to
    /// send frames on it. The happy path: <c>var cam = peer.AddVideoTrack(); await cam.SendFrameAsync(frame, ts);</c>.
    /// </summary>
    /// <remarks>
    /// Backward-compatible semantics: <see cref="WebRtcConfiguration.EnableVideo"/> stays the implicit primary
    /// video track (unchanged SDP); <c>AddVideoTrack</c> adds a further track (or the first, when
    /// <c>EnableVideo</c> is false). The frameless
    /// <see cref="SendVideoFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
    /// keeps addressing the primary track. A track added before the first offer is negotiated in that offer; a
    /// track added mid-call is pending until the next <see cref="CreateOffer"/>/<see cref="SetRemoteDescriptionAsync"/>
    /// cycle applies it to the running session (RFC 8829 renegotiation).
    /// </remarks>
    /// <returns>A handle to send encoded frames on the new track.</returns>
    /// <exception cref="InvalidOperationException">The peer is closed.</exception>
    IVideoTrack AddVideoTrack();

    /// <summary>
    /// Adds a video track with deeper control — direction, codecs, send-side simulcast layers, and the
    /// MediaStream it belongs to (see <see cref="VideoTrackOptions"/>). See
    /// <see cref="AddVideoTrack()"/> for the backward-compatibility and mid-call renegotiation semantics.
    /// </summary>
    /// <param name="options">The track's direction, codecs, simulcast layers, and stream id.</param>
    /// <returns>A handle to send encoded frames on the new track.</returns>
    /// <exception cref="InvalidOperationException">The peer is closed.</exception>
    IVideoTrack AddVideoTrack(VideoTrackOptions options);

    /// <summary>
    /// Adds an audio track (its own <c>m=audio</c> line on the shared BUNDLE transport, beyond the primary audio
    /// anchor) before the first offer, returning a handle to send payloads on it. The happy path:
    /// <c>var extra = peer.AddAudioTrack(); await extra.SendFrameAsync(payload, ts);</c>. The SFU pattern of one
    /// audio stream per remote participant on a single peer connection.
    /// </summary>
    /// <remarks>
    /// The peer's implicit primary audio track stays the always-on transport anchor; <c>AddAudioTrack</c> adds a
    /// further audio m-line. The frameless <see cref="SendAudioAsync"/> keeps addressing the primary track, and
    /// DTMF (<see cref="SendDtmfAsync"/>, RFC 4733) stays on that primary track. A track added before
    /// <see cref="CreateOffer"/> is negotiated in the first offer; a track added mid-call is pending until the next
    /// offer/answer cycle applies it to the running session (RFC 8829 renegotiation). Throws only after the peer is
    /// closed.
    /// </remarks>
    /// <returns>A handle to send encoded audio payloads on the new track.</returns>
    /// <exception cref="InvalidOperationException">The peer is closed.</exception>
    IAudioTrack AddAudioTrack();

    /// <summary>
    /// Adds an audio track with deeper control — direction, codecs, and the MediaStream it belongs to (see
    /// <see cref="AudioTrackOptions"/>) — before the first offer. See <see cref="AddAudioTrack()"/> for the
    /// primary-anchor semantics and the mid-call renegotiation behaviour.
    /// </summary>
    /// <param name="options">The track's direction, codecs, and stream id.</param>
    /// <returns>A handle to send encoded audio payloads on the new track.</returns>
    /// <exception cref="InvalidOperationException">The peer is closed.</exception>
    IAudioTrack AddAudioTrack(AudioTrackOptions options);

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
    /// Asks the peer to send a fresh video key frame (RFC 4585 §6.3.1 PLI). This is the receiving side's
    /// counterpart to <see cref="VideoKeyFrameRequested"/>: call it when a newly attached renderer or a decoder
    /// reset needs an intra frame to start/recover decoding, independent of automatic loss-driven feedback.
    /// Tolerant by design — a no-op returning <see langword="false"/> when no BUNDLE session is negotiated yet,
    /// the bundle has no video track, the peer did not advertise PLI (<c>a=rtcp-fb … nack pli</c>), or the
    /// built-in 500&#160;ms throttle still holds; returns <see langword="true"/> when a PLI was sent. Safe to
    /// call from any thread and after disposal (a no-op).
    /// </summary>
    /// <param name="cancellationToken">Cancels the RTCP send.</param>
    /// <returns><see langword="true"/> when a PLI was sent to the peer; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> RequestVideoKeyFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the peer to send a fresh video key frame on the track identified by <paramref name="mid"/> (RFC 4585
    /// §6.3.1 PLI) — the multi-track overload of <see cref="RequestVideoKeyFrameAsync(CancellationToken)"/>. Use
    /// it when the bundle carries several video tracks and only one needs an intra frame (a renderer attach or a
    /// decoder reset on that track). Tolerant by design — a no-op returning <see langword="false"/> when no
    /// BUNDLE session is negotiated yet, no track carries that MID, the peer did not advertise PLI
    /// (<c>a=rtcp-fb … nack pli</c>), or the built-in throttle still holds; returns <see langword="true"/> when a
    /// PLI was sent. Safe to call from any thread and after disposal (a no-op).
    /// </summary>
    /// <param name="mid">The media identification (<c>a=mid</c>) of the video track to request a key frame for.</param>
    /// <param name="cancellationToken">Cancels the RTCP send.</param>
    /// <returns><see langword="true"/> when a PLI was sent to the peer; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is null or empty.</exception>
    ValueTask<bool> RequestVideoKeyFrameAsync(string mid, CancellationToken cancellationToken = default);

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
