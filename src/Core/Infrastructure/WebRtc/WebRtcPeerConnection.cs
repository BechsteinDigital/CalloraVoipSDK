using System.Linq;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// A signalling-neutral WebRTC peer (the entry point of <c>CalloraVoipSdk.WebRtc</c>, ADR-010/founder
/// architecture): it consumes and produces SDP, mirroring the W3C <c>RTCPeerConnection</c>, so any
/// signalling transport (SIP-over-WebSocket, a custom channel, …) can carry the descriptions. It does
/// not touch the SIP call path. It negotiates BUNDLE (RFC 8843), DTLS-SRTP (RFC 5763), rtcp-mux (RFC 8834),
/// and the MID SDES extension (RFC 9143) via the SDP negotiator, runs the <see cref="WebRtcConnectionState"/>
/// machine, and builds/attaches the <c>BundledMediaSession</c> media transport and its inbound-track events.
/// <para>
/// Threading contract (HARD-C6, interim): the signalling handshake — <see cref="CreateOffer"/>,
/// <see cref="SetRemoteDescriptionAsync"/>, <see cref="StartAsync"/> — is a single ordered sequence
/// and must be driven by one caller at a time, mirroring the W3C signalling-state serialisation; the
/// internal <c>_sync</c> gate protects the shared fields but does not make out-of-order concurrent
/// signalling meaningful. <see cref="DisposeAsync"/> is part of that same single-caller ordering: it must
/// not race an in-flight <see cref="SetRemoteDescriptionAsync"/>, which builds the media session and hands
/// the pre-bound socket over to it — disposing concurrently could tear down the peer between the bind and
/// the hand-over and orphan or double-dispose the socket. The media hot path (<see cref="SendAudioAsync"/>/<see cref="SendVideoFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>)
/// is hardened against a concurrent <see cref="DisposeAsync"/> (HARD-C6): each send holds a drain lease so
/// dispose waits for in-flight sends before tearing down the media session, and a send begun after
/// dispose throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
internal sealed class WebRtcPeerConnection : IAsyncDisposable
{
    private readonly WebRtcPeerOptions _options;
    private readonly ISdpOfferAnswerNegotiator _negotiator;
    private readonly ISdpSessionParser _parser;
    private readonly ISdpSessionSerializer _serializer;
    private readonly IDtlsSrtpHandshaker _handshaker;
    private readonly DtlsCertificate _certificate;
    private readonly IIceStunProbe? _stunProbe;
    private readonly TurnAllocationProbe? _turnProbe;
    private readonly IMdnsResolver _mdnsResolver;
    private readonly CancellationTokenSource _mdnsLifetime = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcPeerConnection> _logger;
    // Applies a second offer/answer cycle to the live session as a track-set diff (video + additional-audio,
    // RFC 8829 renegotiation, 4.7.0) — no transport/DTLS/ICE/SRTP rebuild. Stateless; used only once a session exists.
    private readonly WebRtcRenegotiator _renegotiator;
    private readonly object _sync = new();

    // Stable WebRTC track identity (a=msid, RFC 8830): one MediaStream carrying one audio and one
    // video track. Generated once per peer so re-offers keep the same stream/track ids.
    private readonly string _mediaStreamId = Guid.NewGuid().ToString("N");
    private readonly string _audioTrackId = Guid.NewGuid().ToString("N");
    private readonly string _videoTrackId = Guid.NewGuid().ToString("N");

    // The audio/video tracks the consumer added at runtime via the public AddAudioTrack/AddVideoTrack surface
    // (4.7.0 N-audio / P2c N-video). Owns both lists, the stable per-track a=msid track ids, and the numeric-MID
    // arithmetic; extracted (WebRtcAddedTrackSet) to keep this file under the size limit. Adding any track switches
    // MediaOptions to the numeric-MID multi-track path (RFC 8843): primary audio 0, added-audio, primary video,
    // added-video. A track added mid-call is pending until the next offer/answer cycle applies the diff to the live
    // session (RFC 8829 renegotiation). The set is self-locking, so the peer no longer holds _sync across these calls.
    private readonly WebRtcAddedTrackSet _addedTracks;

    private WebRtcConnectionState _state = WebRtcConnectionState.New;
    // The RFC 8829 §4.1.3 signalling state (offer/answer half of the lifecycle), separate from the ICE/DTLS
    // transport _state. Guarded by _sync; transitioned via TransitionSignalingTo (event fired outside the lock).
    private WebRtcSignalingState _signalingState = WebRtcSignalingState.Stable;
    private string? _remoteDescription;
    private SdpMsid? _remoteAudioMsid;
    private SdpMsid? _remoteVideoMsid;
    private bool _hasRemoteAudio;
    private bool _hasRemoteVideo;
    // Every remote video m-line the peer will send on (P2c: N tracks), in remote m-line order, each with its
    // MID and a=msid. Empty until a remote description is applied. Guarded by _sync.
    private IReadOnlyList<RemoteVideoTrackInfo> _remoteVideoTracks = [];
    // Every ADDITIONAL remote audio m-line the peer will send on (4.7.0: N audio tracks beyond the primary anchor),
    // each with its MID and a=msid. Empty until a remote description is applied (and for a single-audio remote);
    // the primary audio is surfaced via the mid-less audio path. Guarded by _sync.
    private IReadOnlyList<RemoteAudioTrackInfo> _remoteAudioTracks = [];
    private string? _localDescription;
    private SdpSessionDescription? _localOfferModel;
    private BundledMediaSession? _session;
    private readonly SendDrainGate _sendGate = new();
    // Runs each media send / key-frame request under the drain lease (HARD-C6). Lock-free: it reads the live
    // session behind a snapshot delegate that takes _sync here, so the peer keeps sole ownership of its guarded
    // state (extracted to keep this file under the size limit).
    private readonly WebRtcSendLease _sendLease;
    private UdpClient? _mediaSocket;
    private bool _socketHandedOver;
    private bool _started;
    // The relay allocation gathered on the media socket (RFC 8656), retained so the relay coordinator can
    // adopt it post-Start without re-allocating: the allocation is keyed to the socket's 5-tuple, which
    // survives the hand-over to the transport. Holds the first successful allocation and its TURN server; _sync-guarded.
    private (IPEndPoint ServerEndPoint, TurnAllocateResult Allocation)? _gatheredRelay;
    // Trickle ICE (RFC 8838): receives remote candidates, buffering those that arrive before the session exists
    // and routing them to the check list on AttachSession, then routing later ones live. Parsing + mDNS live in
    // the collaborator (keeps this file under the size limit); it owns its own no-loss buffering gate.
    private readonly WebRtcTrickleIceReceiver _trickleIce;
    // Serialises gathered local candidates to their RFC 8829 line and dispatches each to the trickle handler,
    // isolating a throwing app callback from the media core (extracted to keep this file under the size limit).
    private readonly WebRtcLocalCandidateEmitter _candidateEmitter;
    // Bridges a built session's transport-lifecycle/inbound-media events onto this peer's surface and fires the
    // connection-/signalling-state events; pure event fan-out (holds no state, takes no lock, extracted for the
    // size limit) — the peer keeps sole ownership of the _sync-guarded state.
    private readonly WebRtcSessionEventBridge _sessionEvents;

    /// <summary>Raised when the connection state changes (RFC 8829 <c>connectionstatechange</c>).</summary>
    public event Action<WebRtcConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised when the RFC 8829 §4.1.3 signalling state changes (W3C <c>signalingstatechange</c>). The answerer
    /// fires twice within one <see cref="SetRemoteDescriptionAsync"/> — HaveRemoteOffer then back to Stable.
    /// </summary>
    public event Action<WebRtcSignalingState>? SignalingStateChanged;

    /// <summary>
    /// Raised per inbound audio RTP payload on the <em>primary</em> audio track (transport-only; the app owns the
    /// codec). Never fires for an additional audio m-line — use <see cref="AudioTrackFrameReceived"/> for those.
    /// </summary>
    public event Action<byte[]>? AudioReceived;

    /// <summary>
    /// Raised per inbound audio RTP payload tagged with its track MID (4.7.0: N remote audio tracks). Fires only
    /// for the additional tracks, never the primary (which stays on the mid-less <see cref="AudioReceived"/>).
    /// </summary>
    public event Action<string, byte[]>? AudioTrackFrameReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    /// <summary>
    /// Raised per reassembled inbound video frame tagged with its track MID (P2c) — MID, frame, RTP timestamp,
    /// is-key-frame — so the receiver routes a frame to the right <see cref="WebRtc.RemoteTrack"/> when several
    /// remote video m-lines share the bundle.
    /// </summary>
    public event Action<string, byte[], uint, bool>? VideoTrackFrameReceived;

    /// <summary>
    /// Raised per reassembled inbound simulcast-layer frame (4.7.0, RFC 8853/8852) — MID, the layer's
    /// <c>a=rid</c>, frame, RTP timestamp, is-key-frame — the recv-side simulcast / SFU-forwarding surface. Fires
    /// <em>only</em> for RID-tagged layers, never the primary RID-less stream (on <see cref="VideoTrackFrameReceived"/>).
    /// </summary>
    public event Action<string, string, byte[], uint, bool>? VideoLayerFrameReceived;

    /// <summary>
    /// Raised when the peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104); the app should
    /// encode and send a key frame.
    /// </summary>
    public event Action? VideoKeyFrameRequested;

    /// <summary>
    /// Raised once per fully received inbound DTMF tone (RFC 4733 telephone-event) with the tone code (0–15) and
    /// duration in milliseconds. Telephone-event packets are consumed here, never surfaced on <see cref="AudioReceived"/>.
    /// </summary>
    public event Action<byte, int>? DtmfReceived;

    /// <summary>
    /// Raised as each local ICE candidate is gathered (RFC 8838 trickle), carrying the RFC 8829
    /// <c>candidate:</c> line so the app can signal it out-of-band: the host candidate right after the
    /// offer/answer, then server-reflexive and relay candidates from <see cref="GatherCandidatesAsync"/> when
    /// STUN / UDP TURN servers are configured.
    /// </summary>
    public event Action<string>? LocalIceCandidateDiscovered;

    public WebRtcPeerConnection(
        WebRtcPeerOptions options,
        ISdpOfferAnswerNegotiator negotiator,
        ISdpSessionParser parser,
        ISdpSessionSerializer serializer,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory,
        IIceStunProbe? stunProbe = null,
        TurnAllocationProbe? turnProbe = null,
        IMdnsResolver? mdnsResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.LocalEndPoint);
        ArgumentNullException.ThrowIfNull(options.AudioCodecs);
        ArgumentNullException.ThrowIfNull(options.Dtls);
        ArgumentNullException.ThrowIfNull(options.Ice);
        _negotiator = negotiator ?? throw new ArgumentNullException(nameof(negotiator));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _handshaker = handshaker ?? throw new ArgumentNullException(nameof(handshaker));
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _stunProbe = stunProbe;
        _turnProbe = turnProbe;
        _mdnsResolver = mdnsResolver ?? new SystemMdnsResolver();
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<WebRtcPeerConnection>();
        _renegotiator = new WebRtcRenegotiator(_loggerFactory);
        // The config primary video count (0 or 1) is fixed for the peer's lifetime, so the added-track set can do
        // the numeric-MID arithmetic without re-reading _options; it captures it once here.
        _addedTracks = new WebRtcAddedTrackSet(_options.VideoTracks.Count);
        _trickleIce = new WebRtcTrickleIceReceiver(_mdnsResolver, _mdnsLifetime.Token, _logger);
        // Snapshot the public event on each emission so a late subscriber is honoured and the current handler
        // is captured atomically (the event field may be reassigned between candidates).
        _candidateEmitter = new WebRtcLocalCandidateEmitter(() => LocalIceCandidateDiscovered, _logger);
        _sessionEvents = new WebRtcSessionEventBridge(_logger);
        // The send-lease runner reads the live session under _sync via this snapshot delegate; the lock stays here.
        _sendLease = new WebRtcSendLease(_sendGate, () => { lock (_sync) { return _session; } });
    }

    /// <summary>The current connection state.</summary>
    public WebRtcConnectionState State
    {
        get { lock (_sync) { return _state; } }
    }

    /// <summary>The current RFC 8829 §4.1.3 signalling state (offer/answer half of the lifecycle).</summary>
    public WebRtcSignalingState SignalingState
    {
        get { lock (_sync) { return _signalingState; } }
    }

    /// <summary>The applied remote SDP offer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? RemoteDescription
    {
        get { lock (_sync) { return _remoteDescription; } }
    }

    /// <summary>The generated local SDP answer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? LocalDescription
    {
        get { lock (_sync) { return _localDescription; } }
    }

    /// <summary>
    /// The bound local media endpoint. Early-bind binds the media socket at <see cref="CreateOffer"/> /
    /// <see cref="SetRemoteDescriptionAsync"/> — before the session exists — so this exposes the bound socket's
    /// endpoint in that window and the transport's endpoint once the session is built. Null only before the bind.
    /// </summary>
    public IPEndPoint? LocalMediaEndPoint
    {
        get { lock (_sync) { return _session?.LocalEndPoint ?? _mediaSocket?.Client.LocalEndPoint as IPEndPoint; } }
    }

    /// <summary>The selected remote media endpoint of the shared transport, or null before one is set.</summary>
    public IPEndPoint? RemoteMediaEndPoint
    {
        get { lock (_sync) { return _session?.RemoteEndPoint; } }
    }

    /// <summary>
    /// The TURN relay allocation gathered on the media socket during <see cref="GatherCandidatesAsync"/> (its TURN
    /// server endpoint and the allocation — relayed endpoint, lifetime, effective realm/nonce credentials), or null
    /// when none was gathered. Retained so the relay coordinator can adopt it post-Start without re-allocating: it
    /// is keyed to the media socket's 5-tuple, preserved across the hand-over to the transport.
    /// </summary>
    internal (IPEndPoint ServerEndPoint, TurnAllocateResult Allocation)? GatheredRelayAllocation
    {
        get { lock (_sync) { return _gatheredRelay; } }
    }

    /// <summary>
    /// The remote peer's (primary) audio-track identity (a=msid, RFC 8830) from the applied remote description, or
    /// null before one is applied or when the remote advertised no audio msid. This is the remote stream's identity
    /// (what the W3C track model surfaces on the receiver), not this peer's own local msid.
    /// </summary>
    public SdpMsid? RemoteAudioMsid
    {
        get { lock (_sync) { return _remoteAudioMsid; } }
    }

    /// <summary>The remote peer's video-track identity (a=msid), or null. See <see cref="RemoteAudioMsid"/>.</summary>
    public SdpMsid? RemoteVideoMsid
    {
        get { lock (_sync) { return _remoteVideoMsid; } }
    }

    /// <summary>
    /// Every remote video m-line that will send to us (P2c: N tracks), in remote m-line order, each with its MID
    /// and a=msid. Empty before a remote description is applied or for an audio-only remote. Lets the receiver
    /// materialise one <see cref="WebRtc.RemoteTrack"/> per remote video m-line — not just the first.
    /// </summary>
    public IReadOnlyList<RemoteVideoTrackInfo> RemoteVideoTracks
    {
        get { lock (_sync) { return _remoteVideoTracks; } }
    }

    /// <summary>
    /// Every <em>additional</em> remote audio m-line that will send to us (4.7.0: N audio tracks beyond the primary
    /// anchor — the SFU pattern), each with its MID and a=msid; empty for a single-audio remote. The receiver
    /// materialises one <see cref="WebRtc.RemoteTrack"/> per entry; the primary comes from the mid-less audio path.
    /// </summary>
    public IReadOnlyList<RemoteAudioTrackInfo> RemoteAudioTracks
    {
        get { lock (_sync) { return _remoteAudioTracks; } }
    }

    /// <summary>
    /// Whether the applied remote description contains a sending audio media line (independent of a=msid), so the
    /// receiver can materialise the primary audio track from the description rather than waiting for the first frame.
    /// </summary>
    public bool HasRemoteAudio
    {
        get { lock (_sync) { return _hasRemoteAudio; } }
    }

    /// <summary>Whether the applied remote description contains a video media line. See <see cref="HasRemoteAudio"/>.</summary>
    public bool HasRemoteVideo
    {
        get { lock (_sync) { return _hasRemoteVideo; } }
    }

    /// <summary>Cumulative transport counters for the media session, or null before a session is built.</summary>
    public BundledMediaStats? GetStats()
    {
        lock (_sync) { return _session?.SnapshotStats(); }
    }

    /// <summary>
    /// RTCP-derived outbound quality (round-trip time and the loss the peer reports on our media, RFC 3550
    /// §6.4.1), or null before a session is built. Both metrics inside read null until a matching RTCP report
    /// has been echoed by the peer.
    /// </summary>
    public BundledMediaQuality? GetQuality()
    {
        lock (_sync) { return _session?.SnapshotQuality(); }
    }

    /// <summary>
    /// RTCP-derived quality per media stream (CF-004f): RTT and the loss the peer reports on our media keyed per
    /// our sending SSRC and folded onto the audio/video MID, plus our local receive-side jitter (RFC 3550 §A.8)
    /// per remote inbound source. Empty before a session is built or before any metric is available.
    /// </summary>
    public IReadOnlyList<BundledStreamQuality> GetStreamQuality()
    {
        lock (_sync) { return _session?.SnapshotStreamQuality() ?? []; }
    }

    /// <summary>
    /// Adds an audio track to offer as its own <c>m=audio</c> line on the shared BUNDLE transport (4.7.0),
    /// before the first offer/answer, and returns the track's numeric MID. Adding a track switches the peer
    /// onto the numeric-MID multi-track offer path (RFC 8843): the primary audio m-line becomes MID <c>0</c>,
    /// then each added audio m-line follows (MID <c>1</c>, <c>2</c>, …) BEFORE any video m-line. The primary
    /// audio anchor (MID <c>0</c>, the ICE/DTLS transport carrier) is never an added track.
    /// </summary>
    /// <param name="track">The track's codecs, direction, and MediaStream id.</param>
    /// <returns>The numeric MID assigned to the track (stable for its lifetime).</returns>
    /// <remarks>
    /// Mid-call add (RFC 8829 renegotiation, Slice 3 DiffAudio): a track added after the first offer/answer is
    /// pending — the track is recorded but the session is not mutated here (W3C: no track flows until the next
    /// <see cref="CreateOffer"/> → <see cref="SetRemoteDescriptionAsync"/> cycle applies the diff to the live
    /// session). MIDs are stable: an added track keeps its index-derived numeric MID across re-offers. Because
    /// added-audio m-lines precede the video m-lines, adding one shifts every video track's numeric MID up by one.
    /// </remarks>
    public string AddAudioTrack(WebRtcAddedAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        // The closed guard reads the _sync-guarded signalling state; the added-track set is self-locking, so the
        // record itself happens outside _sync (the set interacts with no other peer state — see WebRtcAddedTrackSet).
        lock (_sync)
        {
            if (_signalingState == WebRtcSignalingState.Closed)
                throw new InvalidOperationException("Cannot add an audio track after the peer is closed.");
        }

        return _addedTracks.AddAudio(track);
    }

    /// <summary>
    /// Adds a video track to offer as its own <c>m=video</c> line on the shared BUNDLE transport (P2c),
    /// before the first offer/answer, and returns the track's numeric MID. Adding a track switches the peer
    /// onto the numeric-MID multi-track offer path (RFC 8843): the audio m-line becomes MID <c>0</c>, any
    /// added-audio m-lines follow, the config-time <c>EnableVideo</c> primary video (if any) comes next, then
    /// each added video track in order.
    /// </summary>
    /// <param name="track">The track's codecs, direction, simulcast layers, and MediaStream id.</param>
    /// <returns>The numeric MID assigned to the track (stable for its lifetime).</returns>
    /// <remarks>
    /// Mid-call add (RFC 8829 renegotiation, P3b-3): a track added after the first offer/answer is pending —
    /// the track is recorded but the session is not mutated here (W3C: no track flows until the next
    /// <see cref="CreateOffer"/> → <see cref="SetRemoteDescriptionAsync"/> cycle applies the diff to the live
    /// session). MIDs are stable: an added track keeps its index-derived numeric MID across re-offers.
    /// </remarks>
    public string AddVideoTrack(WebRtcAddedVideoTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_sync)
        {
            if (_signalingState == WebRtcSignalingState.Closed)
                throw new InvalidOperationException("Cannot add a video track after the peer is closed.");
        }

        return _addedTracks.AddVideo(track);
    }

    /// <summary>
    /// Creates a local WebRTC offer (RFC 8829 createOffer + setLocalDescription): BUNDLE, DTLS-SRTP,
    /// rtcp-mux, and the sdes:mid extension. It becomes <see cref="LocalDescription"/>; apply the peer's
    /// answer with <see cref="SetRemoteDescriptionAsync"/> to establish media.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The signalling state is not <see cref="WebRtcSignalingState.Stable"/> or
    /// <see cref="WebRtcSignalingState.HaveLocalOffer"/> — an offer cannot be produced while a remote offer
    /// is pending or after the peer is closed (RFC 8829 §4.1.3).
    /// </exception>
    public string CreateOffer()
    {
        var local = EnsureLocalMediaEndPoint();
        var offerModel = _negotiator.CreateOffer(
            local, _options.AudioCodecs, SdpMediaDirection.SendRecv, MediaOptions(local));
        var offerSdp = _serializer.Serialize(offerModel);
        bool enteredHaveLocalOffer;
        lock (_sync)
        {
            // RFC 8829 §4.1.3: createOffer + setLocalDescription is valid from stable (first offer) and is
            // idempotent from have-local-offer (re-offer before any answer replaces the pending offer, state
            // unchanged). Any other state (a remote offer is pending, or the peer is closed) is an invalid
            // transition and fails loudly rather than silently overwriting negotiation state.
            if (_signalingState is not (WebRtcSignalingState.Stable or WebRtcSignalingState.HaveLocalOffer))
                throw new InvalidOperationException(
                    $"Cannot create an offer in signalling state '{_signalingState}': an offer is valid only " +
                    "from Stable or HaveLocalOffer (RFC 8829 §4.1.3).");

            _localOfferModel = offerModel;
            _localDescription = offerSdp;
            enteredHaveLocalOffer = _signalingState == WebRtcSignalingState.Stable;
            _signalingState = WebRtcSignalingState.HaveLocalOffer;
        }

        // Only the Stable → HaveLocalOffer edge is a transition; a re-offer within HaveLocalOffer fires no event.
        if (enteredHaveLocalOffer)
            RaiseSignalingState(WebRtcSignalingState.HaveLocalOffer);
        _candidateEmitter.EmitLocalHost(local);
        return offerSdp;
    }

    /// <summary>
    /// Applies the peer's remote description and returns this peer's local description. As the answerer (no local
    /// offer) the remote description is an offer: this negotiates and returns the WebRTC answer (RFC 8829
    /// setRemoteDescription → createAnswer). As the offerer (after <see cref="CreateOffer"/>) it is the answer:
    /// applied, and the existing offer returned. Either way the shared BUNDLE media transport is built from the two
    /// descriptions and the peer moves to <see cref="WebRtcConnectionState.Connecting"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The remote description is missing or not valid SDP.</exception>
    /// <exception cref="InvalidOperationException">As the answerer, no answer could be negotiated.</exception>
    public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSdp);
        cancellationToken.ThrowIfCancellationRequested();

        SdpSessionDescription remote;
        try
        {
            remote = _parser.Parse(remoteSdp);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The remote description is not valid SDP.", nameof(remoteSdp), ex);
        }

        SdpSessionDescription pendingOffer;
        string? pendingLocalDescription;
        bool renegotiate;
        lock (_sync)
        {
            // A second offer/answer cycle on a running session is renegotiation (RFC 8829, P3b-3): the shared
            // transport/DTLS/ICE/SRTP is kept and only the video-track diff is applied. Take the renegotiation path
            // instead of rebuilding (an ICE restart, i.e. a rotated ICE ufrag, is still rejected there — dispose
            // and re-create the peer for that). Decided under the lock; dispatched below, outside it, so the diff
            // apply (which takes the session's own gate) never runs under the peer lock (K3 / session-build pattern).
            renegotiate = _session is not null;

            // A session that was already started but never built (StartAsync on a non-bundle exchange) has no
            // renegotiation path — the diff needs a live session. Fail loudly rather than rebuild mid-flight.
            if (!renegotiate && _started)
                throw new InvalidOperationException(
                    "Cannot apply a remote description after StartAsync without a media session; " +
                    "dispose this peer and create a new one.");
        }

        if (renegotiate)
            return RenegotiateAsync(remoteSdp, remote);

        lock (_sync)
        {
            // RFC 8829 §4.1.3: setRemoteDescription is valid as the offerer applying the peer's answer (from
            // HaveLocalOffer) or as the answerer applying the peer's offer (from Stable). HaveRemoteOffer means
            // an answer is already being produced, and Closed means the peer is torn down — both are invalid
            // transitions and fail loudly rather than corrupt the signalling state.
            if (_signalingState is not (WebRtcSignalingState.Stable or WebRtcSignalingState.HaveLocalOffer))
                throw new InvalidOperationException(
                    $"Cannot apply a remote description in signalling state '{_signalingState}' (RFC 8829 §4.1.3).");

            // Capture the offerer state as one snapshot: the local description belongs to _localOfferModel
            // and must be read under the same gate, not unsynchronised afterwards (HARD-C6). The offerer/answerer
            // role is the same discriminator the session build uses (a local offer was created), so the
            // signalling state stays consistent with the transport path actually taken.
            pendingOffer = _localOfferModel!;
            pendingLocalDescription = _localDescription;

            // Answerer (no local offer): the remote description is an offer. Enter HaveRemoteOffer now so the
            // W3C two-transition answerer path (Stable → HaveRemoteOffer → Stable) is observable; the event is
            // fired below, outside the lock. The offerer stays in HaveLocalOffer until the answer is applied.
            if (pendingOffer is null)
                _signalingState = WebRtcSignalingState.HaveRemoteOffer;
        }

        // Answerer's first transition: fire outside the lock (K3). A negotiation failure below still leaves the
        // peer in HaveRemoteOffer, mirroring W3C where a failed createAnswer does not roll signalling back.
        if (pendingOffer is null)
            RaiseSignalingState(WebRtcSignalingState.HaveRemoteOffer);

        SdpSessionDescription localModel;
        string localSdp;
        IPEndPoint? answererLocal = null;
        if (pendingOffer is not null)
        {
            // Offerer: the remote description is the answer; our offer is the local description.
            localModel = pendingOffer;
            localSdp = pendingLocalDescription!;
        }
        else
        {
            // Answerer: the remote description is the offer; negotiate our answer.
            var local = EnsureLocalMediaEndPoint();
            answererLocal = local;
            var result = _negotiator.NegotiateAnswer(
                remote, local, _options.AudioCodecs, SdpMediaDirection.SendRecv, MediaOptions(local));
            if (!result.Success || result.Answer is null)
            {
                TransitionTo(WebRtcConnectionState.Failed);
                throw new InvalidOperationException("Could not negotiate an answer for the remote description.");
            }

            localModel = result.Answer;
            localSdp = _serializer.Serialize(result.Answer);
        }

        // Build the shared media transport from the two descriptions (WebRTC is DTLS-SRTP over one
        // BUNDLE group). A non-bundle exchange yields no session — the local description is still
        // returned, but the peer has no transport (logged), which StartAsync then surfaces. The offerer
        // (a local offer was created) holds the ICE controlling role (RFC 8445 §6.1.1).
        // A relay ICE local candidate is offered only when a TURN allocation was already gathered on this socket
        // (the offerer gathers between CreateOffer and applying the answer; the answerer binds its socket here
        // and gathers afterwards, so its allocation is adopted later — a follow-up). The allocation lives on the
        // same socket the session's transport takes over, so the relay data path rides it.
        (IPEndPoint ServerEndPoint, TurnAllocateResult Allocation)? gatheredRelay;
        lock (_sync)
            gatheredRelay = _gatheredRelay;
        var relayIceBindingFactory = gatheredRelay is { } relay
            ? WebRtcRelayBinding.CreateFactory(relay.ServerEndPoint, relay.Allocation, _loggerFactory)
            : null;

        var session = WebRtcSessionFactory.TryCreate(
            remote, localModel, _options, _handshaker, _certificate, _loggerFactory, _mediaSocket,
            iceControlling: pendingOffer is not null,
            relayIceBindingFactory: relayIceBindingFactory);
        if (session is null)
            _logger.LogWarning("The remote description did not negotiate a BUNDLE media session; no transport was built.");

        lock (_sync)
        {
            _remoteDescription = remoteSdp;
            _localDescription = localSdp;
            _session = session;
            // The transport now owns the pre-bound socket (if a session was built); DisposeAsync must not
            // dispose it again.
            _socketHandedOver = session is not null;
            // Retain the remote track identity (a=msid) so the receiver can group inbound tracks by the
            // remote MediaStream (the W3C RTCTrackEvent.streams semantics).
            ApplyRemoteInventory(remote);
            // Both roles settle to Stable now the exchange is complete: the offerer from HaveLocalOffer (answer
            // applied) and the answerer from HaveRemoteOffer (answer produced) — RFC 8829 §4.1.3. The event is
            // fired below, outside the lock (K3).
            _signalingState = WebRtcSignalingState.Stable;
        }

        // Publish _session before wiring its event handlers, so a state-transition callback can never
        // fire against a peer that has not yet recorded the session it belongs to (HARD-C6).
        if (session is not null)
        {
            WireSession(session);
            // Hand the session to the trickle receiver: it drains the candidates buffered before the session
            // existed and routes later ones live, under its own gate so none is lost (RFC 8838).
            _trickleIce.AttachSession(session);
        }

        TransitionTo(WebRtcConnectionState.Connecting);
        RaiseSignalingState(WebRtcSignalingState.Stable);
        if (answererLocal is not null)
            _candidateEmitter.EmitLocalHost(answererLocal);
        return Task.FromResult(localSdp);
    }

    // Applies a second offer/answer cycle to the running session as a video-track diff (RFC 8829 renegotiation,
    // P3b-3): no transport/DTLS/ICE/SRTP rebuild — only AddVideoTrack / SetVideoTrackInactive on the live session.
    // The signalling state runs the same RFC 8829 §4.1.3 transitions as the first cycle (offerer:
    // HaveLocalOffer → Stable; answerer: Stable → HaveRemoteOffer → Stable), but the discriminator is the current
    // signalling state, not "was a local offer created" (that stays set after cycle 1): HaveLocalOffer means a fresh
    // re-offer was created here and this remote is its answer; Stable means this remote is a new offer to answer.
    private Task<string> RenegotiateAsync(string remoteSdp, SdpSessionDescription remote)
    {
        bool isAnswerer;
        BundledMediaSession session;
        SdpSessionDescription newLocalModel;
        string newLocalSdp;
        lock (_sync)
        {
            session = _session!;

            // RFC 8829 §4.1.3: a re-offer is applied from HaveLocalOffer (offerer, our re-offer's answer) or from
            // Stable (answerer, a new remote offer). Any other state is an invalid transition.
            if (_signalingState is not (WebRtcSignalingState.Stable or WebRtcSignalingState.HaveLocalOffer))
                throw new InvalidOperationException(
                    $"Cannot apply a remote description in signalling state '{_signalingState}' (RFC 8829 §4.1.3).");

            isAnswerer = _signalingState == WebRtcSignalingState.Stable;
            // Offerer: our re-offer (already produced by CreateOffer) is the local description and the remote is its
            // answer. Answerer: negotiate below; enter HaveRemoteOffer so the two-transition answerer path is
            // observable (the event fires outside the lock).
            newLocalModel = _localOfferModel!;
            newLocalSdp = _localDescription!;
            if (isAnswerer)
                _signalingState = WebRtcSignalingState.HaveRemoteOffer;
        }

        // Compute + apply the video-track diff on the live session — outside _sync, since AddVideoTrack /
        // SetVideoTrackInactive take the session's own track-mutation gate (K3). The renegotiator rejects an ICE
        // restart (a rotated remote ICE ufrag). A failure leaves the running tracks untouched and the caller sees it.
        IPEndPoint? answererLocal = null;
        if (isAnswerer)
        {
            RaiseSignalingState(WebRtcSignalingState.HaveRemoteOffer);
            answererLocal = EnsureLocalMediaEndPoint();
            try
            {
                newLocalModel = _renegotiator.NegotiateAnswerAndApply(
                    session, remote,
                    new WebRtcRenegotiationAnswerContext(_negotiator, answererLocal, _options.AudioCodecs, MediaOptions(answererLocal)));
            }
            catch
            {
                // A failed re-answer (no answer negotiable, or an ICE restart) throws before any track mutation, so
                // the running session is intact — but leaving the peer in HaveRemoteOffer would strand it there: both
                // the renegotiation entry guard and CreateOffer reject that state, so it could never renegotiate again.
                // Roll signalling back to Stable (the live tracks are still valid) so a later attempt is possible, then
                // surface the failure (not swallowed — re-thrown).
                lock (_sync)
                    _signalingState = WebRtcSignalingState.Stable;
                RaiseSignalingState(WebRtcSignalingState.Stable);
                throw;
            }
            newLocalSdp = _serializer.Serialize(newLocalModel);
        }
        else
        {
            _renegotiator.Apply(session, _renegotiator.ComputeDiff(session, newLocalModel, remote));
        }

        lock (_sync)
        {
            _remoteDescription = remoteSdp;
            _localDescription = newLocalSdp;
            // Refresh the remote track identity/inventory from the new description (P2c: the receiver re-materialises
            // its remote tracks from this). The transport is unchanged; only the advertised track set moved.
            ApplyRemoteInventory(remote);
            // Both roles settle to Stable now the re-exchange is complete (RFC 8829 §4.1.3).
            _signalingState = WebRtcSignalingState.Stable;
        }

        RaiseSignalingState(WebRtcSignalingState.Stable);
        if (answererLocal is not null)
            _candidateEmitter.EmitLocalHost(answererLocal);
        return Task.FromResult(newLocalSdp);
    }

    /// <summary>
    /// Starts the shared transport: the receive loop, the ICE consent loop, and the DTLS handshake.
    /// The connection reaches <see cref="WebRtcConnectionState.Connected"/> once the handshake installs
    /// the SRTP keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built (no remote description, or a non-bundle one).</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        BundledMediaSession? session;
        lock (_sync)
        {
            session = _session;
            // The transport's receive loop now owns the media socket — candidate gathering (which shares
            // that socket) must not run after this point.
            _started = true;
        }
        if (session is null)
            throw new InvalidOperationException("Apply a BUNDLE remote description before starting the peer.");

        return session.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Sends one already-encoded audio RTP payload on the peer's (primary) audio track — the app owns the codec —
    /// suppressed until the handshake keys the transport.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session was built.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => new(_sendLease.SendViaLeaseAsync(s => s.SendAudioAsync(payload, cancellationToken: cancellationToken).AsTask()));

    /// <summary>
    /// Sends one already-encoded audio RTP payload on the additional audio track identified by
    /// <paramref name="mid"/> (4.7.0 N-audio), stamping the explicit <paramref name="rtpTimestamp"/> on the outbound
    /// packets (RFC 3550 §5.1) rather than a cursor value — so an SFU forwarding this stream preserves the source's
    /// timestamp for A/V-sync (unlike the frameless primary <see cref="SendAudioAsync"/>, which stays cursor-based).
    /// Backs the public <see cref="WebRtc.IAudioTrack"/> handle; suppressed until the handshake keys the transport.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no additional audio track with that MID.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendAudioTrackFrameAsync(string mid, ReadOnlyMemory<byte> payload, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendAudioTrackFrameAsync(mid, payload, rtpTimestamp, cancellationToken: cancellationToken).AsTask());

    /// <summary>Packetises and sends one encoded video frame on the peer's (primary) video track.</summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken));

    /// <summary>
    /// Packetises and sends one encoded video frame on a simulcast <paramref name="rid"/> layer (RFC 8853); the
    /// layer must have been offered via the peer's configured simulcast rids.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken));

    /// <summary>
    /// Packetises and sends one encoded video frame on the video track identified by <paramref name="mid"/> (P2c
    /// multi-track: a specific added track); backs the public <see cref="WebRtc.IVideoTrack"/> handle.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track with that MID.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendVideoTrackFrameAsync(string mid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoTrackFrameAsync(mid, encodedFrame, rtpTimestamp, cancellationToken));

    /// <summary>
    /// Packetises and sends one encoded video frame on the <paramref name="mid"/> video track's simulcast
    /// <paramref name="rid"/> layer (RFC 8853). Backs the public <see cref="WebRtc.IVideoTrack"/> simulcast send.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track with that MID.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendVideoTrackFrameAsync(string mid, string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoTrackFrameAsync(mid, rid, encodedFrame, rtpTimestamp, cancellationToken));

    /// <summary>
    /// Asks the peer for a fresh video key frame on the app's demand (RFC 4585 §6.3.1) — an intra frame when a new
    /// renderer or a decoder reset needs one, independent of detected loss. Tolerant: a no-op returning
    /// <see langword="false"/> when the peer is disposing, no session/video track exists, the peer did not advertise
    /// PLI, or the 500 ms throttle holds; <see langword="true"/> when a PLI was sent. Takes a drain lease so the
    /// RTCP send never races session teardown.
    /// </summary>
    public ValueTask<bool> RequestVideoKeyFrameAsync(CancellationToken cancellationToken = default)
        => _sendLease.RequestKeyFrameCoreAsync(
            static (session, ct) => session.RequestVideoKeyFrameAsync(ct), cancellationToken);

    /// <summary>
    /// Asks the peer for a fresh video key frame on the video track identified by <paramref name="mid"/>
    /// (RFC 4585 §6.3.1) — the multi-track overload of <see cref="RequestVideoKeyFrameAsync(CancellationToken)"/>,
    /// targeting one specific track when several video m-lines share the bundle. Same tolerant no-op/false and
    /// drain-lease semantics as the mid-less overload; returns <see langword="true"/> only when a PLI was sent.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is null or empty.</exception>
    public ValueTask<bool> RequestVideoKeyFrameAsync(string mid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        return _sendLease.RequestKeyFrameCoreAsync(
            (session, ct) => session.RequestVideoTrackKeyFrameAsync(mid, ct), cancellationToken);
    }

    /// <summary>
    /// Sends one out-of-band DTMF tone (RFC 4733 telephone-event) on the peer's audio track (suppressed until
    /// the handshake keys the transport). The tone shares the audio stream's RTP timestamp clock.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tone code exceeds 15, or the duration is below the RFC 4733 floor.</exception>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or telephone-event was not negotiated.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default)
        => _sendLease.SendViaLeaseAsync(s => s.SendDtmfAsync(toneCode, durationMs, cancellationToken));

    /// <summary>
    /// Adds a remote ICE candidate that trickled in out-of-band (RFC 8838), given as an RFC 8829 <c>candidate:</c>
    /// line, to the connectivity-check list. The controlling agent runs a real RFC 8445 §7.2.2 check and nominates
    /// it only if it answers and beats the current pair — never trusted by raw priority. Buffered until the session
    /// is built, then fed live; a malformed/unusable candidate is ignored. On a controlled agent (answerer) this is
    /// a no-op — it adopts the pair the controlling peer nominates via its USE-CANDIDATE check.
    /// </summary>
    public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        // Parsing, mDNS (.local) resolution, and no-loss buffering/routing live in the receiver collaborator.
        _trickleIce.Add(candidate);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gathers server-reflexive (RFC 8445 §5.1.1) and relay (RFC 8656) ICE candidates through the pre-bound media
    /// socket, emitting each on <see cref="LocalIceCandidateDiscovered"/> (RFC 8838 trickle). STUN servers yield
    /// srflx candidates; UDP TURN servers yield a relay candidate when the allocation on the media socket succeeds,
    /// retained for later coordinator adoption (<see cref="GatheredRelayAllocation"/>). No-op without matching
    /// probes or servers. Call after the offer/answer is produced and BEFORE <see cref="StartAsync"/> — the queries
    /// share the media socket, which the transport's receive loop takes over once started.
    /// </summary>
    public async Task GatherCandidatesAsync(CancellationToken cancellationToken = default)
    {
        if (_options.IceServers.Count == 0)
            return;

        var local = EnsureLocalMediaEndPoint();
        Socket socket;
        lock (_sync)
        {
            if (_started)
                throw new InvalidOperationException(
                    "Cannot gather ICE candidates after StartAsync — the media socket is owned by the transport's receive loop.");
            socket = _mediaSocket!.Client;
        }

        // The peer keeps ownership of the retained relay allocation and its session; the gatherer only sequences
        // the wire steps (each temporarily runs its own receive loop on the shared media socket, so they must
        // not overlap the transport's post-Start loop).
        var gatherer = new WebRtcCandidateGatherer(_stunProbe, _turnProbe, _logger);
        await gatherer.GatherAsync(
            _options.IceServers, local, socket, _candidateEmitter.Emit, OnRelayGathered, cancellationToken).ConfigureAwait(false);
    }

    // Retains the first successful TURN allocation for the relay coordinator to adopt post-Start; further
    // successes do not replace the retained one. When THIS allocation is the one retained AND a media session
    // already exists — the answerer, which built its session (direct-only, no gathered allocation yet) before
    // gathering — adopt the relay candidate into it now. The offerer gathers before applying the answer, so its
    // session does not exist yet here (adoptInto stays null) and wires the relay at construction from the
    // options factory instead. Returns the raddr/rport base for the relay candidate: the mapped
    // (server-reflexive) base the server reported, else the host base.
    private IPEndPoint OnRelayGathered(IPEndPoint serverEndPoint, TurnAllocateResult allocation, IPEndPoint local)
    {
        BundledMediaSession? adoptInto = null;
        lock (_sync)
        {
            if (_gatheredRelay is null)
            {
                _gatheredRelay = (serverEndPoint, allocation);
                adoptInto = _session;
            }
        }

        // Adopt outside the lock: AdoptRelay builds the TURN control stack and takes the ICE driver's own gate,
        // and needs no _sync-guarded state of ours. AdoptRelay is idempotent, so a session that already wired
        // a relay (it should not on the answerer, but defensively) is unaffected.
        adoptInto?.AdoptRelay(WebRtcRelayBinding.CreateFactory(serverEndPoint, allocation, _loggerFactory));

        return allocation.MappedEndPoint ?? local;
    }

    // Wires the built session's transport-lifecycle and inbound-media events onto this peer via the event bridge.
    // The peer supplies the raise delegates (null-conditional invoke of THIS peer's events); TransitionTo, which
    // owns the _sync-guarded connection state, stays here and is passed as a delegate.
    private void WireSession(BundledMediaSession session)
        => _sessionEvents.WireSession(
            session,
            TransitionTo,
            payload => AudioReceived?.Invoke(payload),
            (mid, payload) => AudioTrackFrameReceived?.Invoke(mid, payload),
            (frame, timestamp, isKeyFrame) => VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame),
            (mid, frame, timestamp, isKeyFrame) => VideoTrackFrameReceived?.Invoke(mid, frame, timestamp, isKeyFrame),
            (mid, rid, frame, timestamp, isKeyFrame) => VideoLayerFrameReceived?.Invoke(mid, rid, frame, timestamp, isKeyFrame),
            () => VideoKeyFrameRequested?.Invoke(),
            (toneCode, durationMs) => DtmfReceived?.Invoke(toneCode, durationMs));

    // The SDP media options for the offer/answer, assembled by WebRtcSdpOptionsBuilder (extracted to keep this
    // file under the size limit): the 1+1 semantic-MID path when no track was added (byte-identical to pre-P2c),
    // else the numeric-MID multi-track path (added-audio and/or added-video). The added tracks are snapshotted
    // by the self-locking WebRtcAddedTrackSet, in the m-line order the numeric MIDs were assigned in.
    private SdpMediaOptions MediaOptions(IPEndPoint local)
        => WebRtcSdpOptionsBuilder.Build(
            local,
            _options,
            _addedTracks.SnapshotAudio(),
            _addedTracks.SnapshotVideo(),
            _mediaStreamId,
            _audioTrackId,
            _videoTrackId);

    // Binds the shared media socket up front (Trickle-ICE early-bind) so the offer/answer advertise the real
    // ephemeral port and a host candidate before the session (transport) exists — fixing the zero-port
    // disabled offer. The transport takes ownership at session build; if the peer is disposed before that,
    // DisposeAsync disposes the socket.
    private IPEndPoint EnsureLocalMediaEndPoint()
    {
        lock (_sync)
        {
            if (_mediaSocket is null)
            {
                // Match the socket family to the configured local bind address; binding an IPv4
                // UdpClient to an IPv6 endpoint (or vice versa) throws on family mismatch.
                var socket = new UdpClient(_options.LocalEndPoint.AddressFamily);
                // Kernel SO_RCVBUF for the shared media socket; sized for video bitrates, not the max
                // datagram (MediaSocketDefaults keeps those two concerns separate).
                socket.Client.ReceiveBufferSize = MediaSocketDefaults.SocketReceiveBufferBytes;
                socket.Client.Bind(_options.LocalEndPoint);
                _mediaSocket = socket;
            }

            return (IPEndPoint)_mediaSocket.Client.LocalEndPoint!;
        }
    }

    // Records the remote track facts (a=msid identity, has-audio/has-video, the per-m-line sending video
    // inventory, derived by WebRtcRemoteMediaInventory) from a newly-applied remote description, so the receiver
    // materialises its remote tracks from it. Shared by the first cycle and renegotiation. The CALLER MUST hold
    // _sync (it writes the guarded fields directly); it never re-locks.
    private void ApplyRemoteInventory(SdpSessionDescription remote)
    {
        var inventory = WebRtcRemoteMediaInventory.FromRemoteDescription(remote);
        _hasRemoteAudio = inventory.HasRemoteAudio;
        _hasRemoteVideo = inventory.HasRemoteVideo;
        _remoteAudioMsid = inventory.AudioMsid;
        _remoteVideoMsid = inventory.VideoMsid;
        _remoteAudioTracks = inventory.AudioTracks;
        _remoteVideoTracks = inventory.VideoTracks;
    }

    private void TransitionTo(WebRtcConnectionState next)
    {
        lock (_sync)
        {
            if (_state == next || _state == WebRtcConnectionState.Closed)
                return;
            _state = next;
        }

        // The _state compare-and-set stays here under _sync; only the outside-the-lock event raise (identical
        // snapshot-and-invoke, throwing handler logged not propagated) is delegated to the bridge.
        _sessionEvents.RaiseConnectionState(ConnectionStateChanged, next);
    }

    // Fires the signalling-state change event for a transition already committed to _signalingState under
    // _sync at the call site. The delegate is snapshotted inside the lock and invoked outside it (K3), so a
    // handler can re-subscribe without deadlocking; a throwing handler is logged, not propagated (K3: handlers
    // must not break the signalling path).
    private void RaiseSignalingState(WebRtcSignalingState next)
    {
        Action<WebRtcSignalingState>? handler;
        lock (_sync) { handler = SignalingStateChanged; }
        _sessionEvents.RaiseSignalingState(handler, next);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Cancel any in-flight background mDNS (.local) resolutions so they cannot outlive the peer.
        // Idempotent: a second dispose sees an already-disposed CTS (Cancel would throw) — swallow it.
        try { _mdnsLifetime.Cancel(); }
        catch (ObjectDisposedException) { _logger.LogTrace("mDNS lifetime CTS already disposed (double dispose)."); }

        BundledMediaSession? session;
        UdpClient? orphanSocket;
        bool signalingClosed;
        lock (_sync)
        {
            session = _session;
            _session = null;
            // If the early-bound socket was never handed to a transport, this peer still owns it and must
            // dispose it; once handed over, the session/transport owns it. Null it out so a second dispose
            // never double-disposes.
            orphanSocket = _socketHandedOver ? null : _mediaSocket;
            _mediaSocket = null;
            // Signalling terminates at Closed (RFC 8829 §4.1.3), idempotent across a double dispose. The event
            // is fired below, outside the lock (K3).
            signalingClosed = _signalingState != WebRtcSignalingState.Closed;
            _signalingState = WebRtcSignalingState.Closed;
        }

        TransitionTo(WebRtcConnectionState.Closed);
        if (signalingClosed)
            RaiseSignalingState(WebRtcSignalingState.Closed);

        // Refuse new sends and wait for in-flight ones to finish before tearing down the session, so a
        // concurrent send never operates on a disposed media session (HARD-C6). Idempotent: a second
        // dispose sees a null session and an already-drained gate. Drain completion is bounded by the
        // in-flight sends: a send that never completes (unbounded blocking, an un-cancelled token) keeps
        // dispose waiting — callers wanting a bounded teardown must cancel pending sends first.
        await _sendGate.BeginDrainAsync().ConfigureAwait(false);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        orphanSocket?.Dispose();
        _mdnsLifetime.Dispose();
    }
}
