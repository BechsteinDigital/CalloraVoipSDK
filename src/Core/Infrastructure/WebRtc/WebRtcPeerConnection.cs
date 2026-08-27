using System.Linq;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
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
    private readonly IWebRtcHostCandidateProvider _hostCandidateProvider;
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
    // arithmetic; extracted (WebRtcAddedTrackSet) to keep this file under the size limit. Compatibility mode
    // switches to numeric MIDs on the first added track; stable mode starts numeric and appends in API call order.
    // A mid-call track remains pending until the next offer/answer cycle applies the live diff (RFC 8829).
    private readonly WebRtcAddedTrackSet _addedTracks;
    // The transport half of the lifecycle (extracted, sharing _sync so its serialisation is unchanged).
    private readonly WebRtcConnectionStateMachine _connectionState;
    // The RFC 8829 §4.1.3 offer/answer half of the lifecycle — the signalling state and the two descriptions that
    // move with it, with each legal transition named and guarded (extracted; shares _sync, so a transition still
    // commits atomically with the session and inventory writes around it). Separate from the transport state.
    private readonly WebRtcNegotiationState _negotiation;
    // The RFC 8829 offer/answer choreography, extracted: it owns the sequence, the peer owns the state it moves.
    private readonly WebRtcOfferAnswerCycle _offerAnswer;
    // The remote-track facts derived from the applied remote description (a=msid identities, has-audio/has-video,
    // the per-m-line sending inventory). Held as the one immutable record the deriver returns rather than
    // destructured into six fields: swapping one reference under _sync means a reader can never observe a
    // half-updated inventory — six separate writes could be read between any two of them. Guarded by _sync.
    private WebRtcRemoteMediaInventory? _remoteInventory;
    private BundledMediaSession? _session;
    private readonly SendDrainGate _sendGate = new();
    // Runs each media send / key-frame request under the drain lease (HARD-C6). Lock-free: it reads the live
    // session behind a snapshot delegate that takes _sync here, so the peer keeps sole ownership of its guarded
    // state (extracted to keep this file under the size limit).
    private readonly WebRtcSendLease _sendLease;
    // The shared media socket across its one hand-over to the transport (extracted; shares _sync).
    private readonly WebRtcMediaSocketOwner _mediaSocket;
    private bool _started;
    // Owns the gathered TURN relay allocation (RFC 8656), retained for post-Start adoption; see the store.
    private readonly WebRtcRelayAllocationStore _relayAllocation;
    // The TCP/TLS stream relay path (ADR-073): the connector turns a stream TURN entry into a candidate over its
    // own connection; the store retains it first-wins and adopts it into the session (now for the answerer, on
    // build for the offerer). Separate from the UDP _relayAllocation, which rides the shared media socket.
    private readonly WebRtcStreamRelayConnector _streamRelayConnector;
    private readonly WebRtcStreamRelayStore _streamRelay;
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
    // Projects the session's transport-cc congestion signal (transport-cc) onto this peer's public surface; see the relay.
    private readonly WebRtcCongestionRelay _congestion;

    /// <summary>Raised when the connection state changes (RFC 8829 <c>connectionstatechange</c>).</summary>
    public event Action<WebRtcConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised when the RFC 8829 §4.1.3 signalling state changes (W3C <c>signalingstatechange</c>). The answerer
    /// fires twice within one <see cref="SetRemoteDescriptionAsync"/> — HaveRemoteOffer then back to Stable.
    /// </summary>
    public event Action<WebRtcSignalingState>? SignalingStateChanged;

    /// <summary>
    /// Raised per inbound audio RTP payload on the <em>primary</em> audio track (transport-only; the app owns the
    /// codec), with the packet's RTP timestamp (RFC 3550 §5.1) so a receiver/SFU can forward it with a monotonic
    /// clock. Never fires for an additional audio m-line — use <see cref="AudioTrackFrameReceived"/> for those.
    /// </summary>
    public event Action<byte[], uint>? AudioReceived;

    /// <summary>
    /// Raised per inbound audio RTP payload tagged with its track MID (4.7.0: N remote audio tracks), with the
    /// packet's RTP timestamp (RFC 3550 §5.1). Fires only for the additional tracks, never the primary (which
    /// stays on the mid-less <see cref="AudioReceived"/>).
    /// </summary>
    public event Action<string, byte[], uint>? AudioTrackFrameReceived;

    /// <summary>Raised with each reassembled inbound video frame.</summary>
    public event Action<InboundVideoFrame>? VideoFrameReceived;

    /// <summary>
    /// Raised per reassembled inbound video frame tagged with its track MID (P2c), so the receiver routes a
    /// frame to the right <see cref="WebRtc.RemoteTrack"/> when several remote video m-lines share the bundle.
    /// </summary>
    public event Action<string, InboundVideoFrame>? VideoTrackFrameReceived;

    /// <summary>
    /// Raised per reassembled inbound simulcast-layer frame (4.7.0, RFC 8853/8852) — MID, the layer's
    /// <c>a=rid</c>, and the frame — the recv-side simulcast / SFU-forwarding surface. Fires
    /// <em>only</em> for RID-tagged layers, never the primary RID-less stream (on <see cref="VideoTrackFrameReceived"/>).
    /// </summary>
    public event Action<string, string, InboundVideoFrame>? VideoLayerFrameReceived;

    /// <summary>
    /// Raised when the peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104); the app should
    /// encode and send a key frame.
    /// </summary>
    public event Action? VideoKeyFrameRequested;

    /// <summary>
    /// Raised when the peer asks for a key frame on one specific outbound video stream (#227) — the MID and the
    /// media SSRC its PLI/FIR named, with the <c>a=rid</c> layer when that m-line simulcasts. Fires alongside
    /// <see cref="VideoKeyFrameRequested"/>, which stays mid-less for the single-track case.
    /// </summary>
    public event Action<string, VideoKeyFrameRequest>? VideoTrackKeyFrameRequested;

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

    /// <summary>
    /// Raised whenever the sender-side transport-wide congestion control (transport-cc) revises the
    /// recommended outbound bitrate for this peer, carrying the new bitrate (bits/second) and coarse network
    /// quality. Silent when transport-cc was not negotiated. Reactive per feedback report; the app decides when
    /// to act (the SDK does not throttle). Projected from the media session by <see cref="WebRtcCongestionRelay"/>.
    /// </summary>
    public event Action<long, NetworkQuality>? RecommendedBitrateChanged;
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
        IMdnsResolver? mdnsResolver = null, IWebRtcHostCandidateProvider? hostCandidateProvider = null)
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
        WarnOnDegenerateSimulcastConfig();
        _hostCandidateProvider = hostCandidateProvider ?? new SystemWebRtcHostCandidateProvider(
            loggerFactory.CreateLogger<SystemWebRtcHostCandidateProvider>());
        // Peer-lifetime opaque-video policy, captured once so a renegotiated track matches (#223, ADR-068). It also
        // owns the local ICE credentials, because an ICE restart (#226) is the only thing that ever rotates them.
        _renegotiator = new WebRtcRenegotiator(
            _loggerFactory, _options.OpaqueVideoFrames, _options.Ice,
            // A restart re-runs connectivity checks, so the peer goes back to Connecting — including from Failed,
            // which is where a network change that killed consent will already have left it.
            onIceRestarted: () => TransitionTo(WebRtcConnectionState.Connecting));
        _relayAllocation = new WebRtcRelayAllocationStore(_loggerFactory);
        _streamRelayConnector = new WebRtcStreamRelayConnector(new StunMessageCodec(), _loggerFactory);
        _streamRelay = new WebRtcStreamRelayStore(_loggerFactory);
        // The config primary video count (0 or 1) is fixed for the peer's lifetime, so the added-track set can do
        // the numeric-MID arithmetic without re-reading _options; it captures it once here.
        _addedTracks = new WebRtcAddedTrackSet(_options.VideoTracks.Count);
        _trickleIce = new WebRtcTrickleIceReceiver(_mdnsResolver, _mdnsLifetime.Token, _logger);
        // Snapshot the public event on each emission so a late subscriber is honoured and the current handler
        // is captured atomically (the event field may be reassigned between candidates).
        _candidateEmitter = new WebRtcLocalCandidateEmitter(() => LocalIceCandidateDiscovered, _logger);
        _sessionEvents = new WebRtcSessionEventBridge(_logger);
        // Congestion projection reads the live session under _sync via the same snapshot discipline as the lease.
        _congestion = new WebRtcCongestionRelay(() => { lock (_sync) { return _session; } });
        // The send-lease runner reads the live session under _sync via this snapshot delegate; the lock stays here.
        _sendLease = new WebRtcSendLease(_sendGate, () => { lock (_sync) { return _session; } });
        _mediaSocket = new WebRtcMediaSocketOwner(_sync, _options.LocalEndPoint);
        _negotiation = new WebRtcNegotiationState(_sync);
        // The RFC 8829 offer/answer choreography (extracted). Everything guarded stays here, behind the delegates
        // below — each takes _sync itself, so the cycle never holds peer state, only drives it.
        _offerAnswer = new WebRtcOfferAnswerCycle(
            _negotiation, _negotiator, _parser, _serializer, _options.AudioCodecs, _renegotiator, _logger,
            new WebRtcOfferAnswerHost(
                SnapshotSession: () => { lock (_sync) { return (_session, _started); } },
                EnsureLocalEndPoint: () => _mediaSocket.EnsureBound(),
                MediaOptions: MediaOptions,
                BuildSession: (remote, local, iceControlling) => WebRtcSessionFactory.TryCreate(
                    remote, local, _options, _handshaker, _certificate, _loggerFactory, _mediaSocket.Socket,
                    iceControlling, _relayAllocation.BuildOfferFactory()),
                CommitSession: CommitSession,
                OnSessionBuilt: OnSessionBuilt,
                TransitionTo: TransitionTo,
                RaiseSignalingState: RaiseSignalingState,
                EmitLocalHosts: local => _candidateEmitter.EmitLocalHosts(_hostCandidateProvider.GetHostEndPoints(local))));
        // Shares _sync so the transport state keeps its original serialisation; the event raise (snapshot-and-
        // invoke, throwing handler logged not propagated) stays with the bridge and runs outside the lock.
        _connectionState = new WebRtcConnectionStateMachine(
            _sync, next => _sessionEvents.RaiseConnectionState(ConnectionStateChanged, next));
    }

    /// <summary>The current connection state.</summary>
    public WebRtcConnectionState State => _connectionState.Current;

    /// <summary>The current RFC 8829 §4.1.3 signalling state (offer/answer half of the lifecycle).</summary>
    public WebRtcSignalingState SignalingState => _negotiation.Current;

    /// <summary>The applied remote SDP offer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? RemoteDescription => _negotiation.RemoteDescription;

    /// <summary>The generated local SDP answer, or null before <see cref="SetRemoteDescriptionAsync"/>.</summary>
    public string? LocalDescription => _negotiation.LocalDescription;

    /// <summary>
    /// The bound local media endpoint. Early-bind binds the media socket at <see cref="CreateOffer"/> /
    /// <see cref="SetRemoteDescriptionAsync"/> — before the session exists — so this exposes the bound socket's
    /// endpoint in that window and the transport's endpoint once the session is built. Null only before the bind.
    /// </summary>
    /// <summary>
    /// The receive-simulcast rids the peer confirmed on the primary video m-line (RFC 8853 §5.3), read off the
    /// current session under the same lock as the other session-derived reads. Empty before a session exists or
    /// when no receive simulcast was negotiated.
    /// </summary>
    public IReadOnlyList<string> NegotiatedReceiveSimulcastRids
    {
        get { lock (_sync) { return _session?.VideoReceiveRids.ToArray() ?? []; } }
    }

    public IPEndPoint? LocalMediaEndPoint
    {
        get { lock (_sync) { return _session?.LocalEndPoint ?? _mediaSocket.BoundEndPoint; } }
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
        => _relayAllocation.Snapshot;

    /// <summary>
    /// The remote peer's (primary) audio-track identity (a=msid, RFC 8830) from the applied remote description, or
    /// null before one is applied or when the remote advertised no audio msid. This is the remote stream's identity
    /// (what the W3C track model surfaces on the receiver), not this peer's own local msid.
    /// </summary>
    public SdpMsid? RemoteAudioMsid
    {
        get { lock (_sync) { return _remoteInventory?.AudioMsid; } }
    }

    /// <summary>The remote peer's video-track identity (a=msid), or null. See <see cref="RemoteAudioMsid"/>.</summary>
    public SdpMsid? RemoteVideoMsid
    {
        get { lock (_sync) { return _remoteInventory?.VideoMsid; } }
    }

    /// <summary>
    /// Every remote video m-line that will send to us (P2c: N tracks), in remote m-line order, each with its MID
    /// and a=msid. Empty before a remote description is applied or for an audio-only remote. Lets the receiver
    /// materialise one <see cref="WebRtc.RemoteTrack"/> per remote video m-line — not just the first.
    /// </summary>
    public IReadOnlyList<RemoteVideoTrackInfo> RemoteVideoTracks
    {
        get { lock (_sync) { return _remoteInventory?.VideoTracks ?? []; } }
    }

    /// <summary>
    /// Every <em>additional</em> remote audio m-line that will send to us (4.7.0: N audio tracks beyond the primary
    /// anchor — the SFU pattern), each with its MID and a=msid; empty for a single-audio remote. The receiver
    /// materialises one <see cref="WebRtc.RemoteTrack"/> per entry; the primary comes from the mid-less audio path.
    /// </summary>
    public IReadOnlyList<RemoteAudioTrackInfo> RemoteAudioTracks
    {
        get { lock (_sync) { return _remoteInventory?.AudioTracks ?? []; } }
    }

    /// <summary>
    /// Whether the applied remote description contains a sending audio media line (independent of a=msid), so the
    /// receiver can materialise the primary audio track from the description rather than waiting for the first frame.
    /// </summary>
    public bool HasRemoteAudio
    {
        get { lock (_sync) { return _remoteInventory?.HasRemoteAudio ?? false; } }
    }

    /// <summary>Whether the applied remote description contains a video media line. See <see cref="HasRemoteAudio"/>.</summary>
    public bool HasRemoteVideo
    {
        get { lock (_sync) { return _remoteInventory?.HasRemoteVideo ?? false; } }
    }

    /// <summary>Cumulative transport counters for the media session, or null before a session is built.</summary>
    public BundledMediaStats? GetStats()
    {
        lock (_sync) { return _session?.SnapshotStats(); }
    }

    /// <summary>
    /// Point-in-time recommended outbound bitrate (bits/second) and coarse network quality from transport-cc
    /// (transport-cc); each null before a session is built or when transport-cc was not negotiated. Reactive
    /// counterpart: <see cref="RecommendedBitrateChanged"/>. Projected by <see cref="WebRtcCongestionRelay"/>.
    /// </summary>
    public long? RecommendedOutgoingBitrateBps => _congestion.RecommendedOutgoingBitrateBps;

    /// <summary>
    /// Point-in-time coarse outbound network quality from transport-cc (transport-cc); null before a session is
    /// built or when transport-cc was not negotiated. Reactive counterpart: <see cref="RecommendedBitrateChanged"/>.
    /// Projected by <see cref="WebRtcCongestionRelay"/>.
    /// </summary>
    public NetworkQuality? OutgoingNetworkQuality => _congestion.OutgoingNetworkQuality;

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
    /// before the first offer/answer, and returns the track's numeric MID. Compatibility mode groups added audio
    /// immediately after primary audio. Stable mode appends it after every primary m-line and earlier runtime
    /// track so existing m-line identities cannot move during renegotiation. The primary audio anchor is never
    /// an added track.
    /// </summary>
    /// <param name="track">The track's codecs, direction, and MediaStream id.</param>
    /// <returns>The numeric MID assigned to the track (stable for its lifetime).</returns>
    /// <remarks>
    /// Mid-call add (RFC 8829 renegotiation, Slice 3 DiffAudio): a track added after the first offer/answer is
    /// pending — the track is recorded but the session is not mutated here (W3C: no track flows until the next
    /// <see cref="CreateOffer"/> → <see cref="SetRemoteDescriptionAsync"/> cycle applies the diff to the live
    /// session). An added track keeps its assigned numeric MID across re-offers. In compatibility mode, adding
    /// audio after video was already offered can still shift existing video MIDs; enable stable numeric MIDs on
    /// SFU-style peers that add tracks during a session.
    /// </remarks>
    public string AddAudioTrack(WebRtcAddedAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        // The closed guard reads the signalling state under its gate; the added-track set is self-locking, so the
        // record itself happens outside it (the set interacts with no other peer state — see WebRtcAddedTrackSet).
        if (_negotiation.Current == WebRtcSignalingState.Closed)
            throw new InvalidOperationException("Cannot add an audio track after the peer is closed.");

        return _addedTracks.AddAudio(track);
    }

    /// <summary>
    /// Adds a video track to offer as its own <c>m=video</c> line on the shared BUNDLE transport (P2c),
    /// before the first offer/answer, and returns the track's numeric MID. Compatibility mode groups added video
    /// after primary audio, added audio, and primary video. Stable mode appends it after every primary m-line and
    /// earlier runtime track so existing m-line identities cannot move during renegotiation.
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
        if (_negotiation.Current == WebRtcSignalingState.Closed)
            throw new InvalidOperationException("Cannot add a video track after the peer is closed.");

        WarnOnDegenerateSimulcast(track.SimulcastSendRids, track.SimulcastRecvRids, "added track");
        return _addedTracks.AddVideo(track);
    }

    // A simulcast direction with a single distinct RID is not simulcast: the SDP builder drops a lone a=rid
    // (Chrome strips it, RFC 8853, #369), so the track silently degrades to one stream. Surface that once, at
    // the point the configuration enters the peer, rather than leaving the developer to discover it on the wire
    // (HARD-G3: a reduction is observable, never silent). Setup-time only — never on the media path.
    private void WarnOnDegenerateSimulcastConfig()
    {
        for (var i = 0; i < _options.VideoTracks.Count; i++)
            WarnOnDegenerateSimulcast(
                _options.VideoTracks[i].SimulcastSendRids, _options.VideoTracks[i].SimulcastRecvRids, $"track {i}");
    }

    private void WarnOnDegenerateSimulcast(
        IReadOnlyList<string> sendRids, IReadOnlyList<string> recvRids, string context)
    {
        WarnOnSingleSimulcastLayer(sendRids, "send", context);
        WarnOnSingleSimulcastLayer(recvRids, "receive", context);
    }

    private void WarnOnSingleSimulcastLayer(IReadOnlyList<string> rids, string direction, string context)
    {
        if (rids.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).Count() != 1)
            return;

        _logger.LogWarning(
            "Video {Context}: a single simulcast {Direction} RID ({Rids}) was configured, but one a=rid is not " +
            "simulcast (RFC 8853) and is dropped — it falls back to a single stream. Configure two or more " +
            "distinct RIDs, or none.",
            context, direction, string.Join(",", rids));
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
        var local = _mediaSocket.EnsureBound();
        var offerModel = _negotiator.CreateOffer(
            local, _options.AudioCodecs, SdpMediaDirection.SendRecv, MediaOptions(local));
        var offerSdp = _serializer.Serialize(offerModel);
        var enteredHaveLocalOffer = _negotiation.EnterHaveLocalOffer(offerModel, offerSdp);

        // Only the Stable → HaveLocalOffer edge is a transition; a re-offer within HaveLocalOffer fires no event.
        if (enteredHaveLocalOffer)
            RaiseSignalingState(WebRtcSignalingState.HaveLocalOffer);
        _candidateEmitter.EmitLocalHosts(_hostCandidateProvider.GetHostEndPoints(local));
        return offerSdp;
    }

    /// <summary>
    /// Produces an offer that requests an ICE restart (RFC 8445 §9, the W3C <c>createOffer({iceRestart: true})</c>):
    /// the local ICE credentials are rotated and the running agent restarted with them <em>first</em>, so the
    /// returned offer announces a restart that has already taken effect here. Rotating and offering together is
    /// deliberate — an application cannot rotate without sending the offer that announces it, which would leave the
    /// peer checking against credentials nobody honours. Before a session exists this is exactly
    /// <see cref="CreateOffer"/>: a first offer's credentials are new anyway, and there is no agent to restart.
    /// </summary>
    /// <param name="cancellationToken">Cancels before any state is touched.</param>
    /// <returns>The offer SDP to send, carrying the rotated credentials.</returns>
    /// <exception cref="InvalidOperationException">The signalling state does not permit an offer (RFC 8829 §4.1.3).</exception>
    public async Task<string> CreateIceRestartOfferAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BundledMediaSession? session;
        lock (_sync)
            session = _session;
        if (session is not null)
            await _renegotiator.RestartLocalIceAsync(session).ConfigureAwait(false);
        return CreateOffer();
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
        => _offerAnswer.ApplyRemoteAsync(remoteSdp, cancellationToken);


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
    public Task SendVideoFrameAsync(
        ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default, bool? isKeyFrame = null)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken, isKeyFrame));

    /// <summary>
    /// Packetises and sends one encoded video frame on a simulcast <paramref name="rid"/> layer (RFC 8853); the
    /// layer must have been offered via the peer's configured simulcast rids.
    /// </summary>
    /// <exception cref="InvalidOperationException">No BUNDLE media session, or the bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    /// <exception cref="ObjectDisposedException">The peer is disposing or disposed.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default, bool? isKeyFrame = null)
        => _sendLease.SendViaLeaseAsync(s => s.SendVideoFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken, isKeyFrame));

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

        var local = _mediaSocket.EnsureBound();
        BundledMediaSession? live;
        Socket? socket;
        lock (_sync)
        {
            // After StartAsync the receive loop owns the socket, so gathering runs over the live transport — that
            // is what lets an ICE restart re-gather without surrendering the socket that DTLS and SRTP depend on.
            live = _started ? _session : null;
            socket = _started ? null : _mediaSocket.Socket!.Client;
            if (_started && live is null)
                throw new InvalidOperationException("Cannot gather: this peer was started without a media session.");
        }

        // The peer keeps ownership of the retained relay allocation and its session; the gatherer only sequences
        // the wire steps (pre-start each runs its own receive loop on the socket, so they must not overlap).
        var gatherer = new WebRtcCandidateGatherer(_stunProbe, _turnProbe, _logger);
        var hostEndPoints = _hostCandidateProvider.GetHostEndPoints(local);
        var relatedHost = hostEndPoints.Count > 0 ? hostEndPoints[0] : local;
        // Re-emit hosts on a live re-gather: an interface that came up since shares this socket's wildcard port.
        if (live is not null) _candidateEmitter.EmitLocalHosts(hostEndPoints);
        // The relay store latches first-wins and adopts into an already-built (answerer) session; it reads the
        // adopt target through this _sync-guarded session snapshot, so the peer keeps sole ownership of _session.
        await gatherer.GatherAsync(
            _options.IceServers, local, relatedHost, socket, live is null ? null : live.ProbeServerReflexiveAsync,
            _candidateEmitter.Emit,
            (serverEndPoint, allocation, gatheredLocal) => _relayAllocation.OnGathered(
                serverEndPoint, allocation, gatheredLocal, () => { lock (_sync) { return _session; } }),
            cancellationToken).ConfigureAwait(false);

        // Stream relay candidates (TCP/TLS TURN, ADR-073) gather over their own connection — independent of the
        // media socket — so they are gathered here rather than through the socket-centric gatherer above.
        await GatherStreamRelaysAsync(local, relatedHost, cancellationToken).ConfigureAwait(false);
    }

    // Gathers a stream relay candidate for each TCP/TLS TURN server (ADR-073), first-wins: the first that
    // allocates is retained + advertised and adopted into the session (now for the answerer, on build for the
    // offerer); a surplus is disposed. A failed connect/allocation is one fewer candidate, never a throw. Skips
    // entirely once one is retained (an ICE restart re-gather keeps the existing stream connection, like the UDP
    // relay keeps its allocation).
    private async Task GatherStreamRelaysAsync(IPEndPoint local, IPEndPoint relatedHost, CancellationToken ct)
    {
        if (_streamRelay.RelayedEndPoint is not null)
            return;

        foreach (var server in _options.IceServers)
        {
            if (server.Type != IceServerType.Turn || server.Transport == IceTransport.Udp)
                continue;

            var candidate = await _streamRelayConnector
                .ConnectAndGatherAsync(server, local.AddressFamily, onInboundMedia: _ => { }, ct)
                .ConfigureAwait(false);
            if (candidate is null)
                continue;

            // OnGathered latches first-wins and adopts into an already-built (answerer) session, reading the adopt
            // target through the same _sync-guarded snapshot the UDP store uses, so the peer keeps sole ownership.
            if (_streamRelay.OnGathered(candidate, () => { lock (_sync) { return _session; } }))
            {
                _candidateEmitter.Emit(WebRtcIceCandidateFactory.RelayCandidate(candidate.RelayedEndPoint, relatedHost));
                return;
            }

            await candidate.DisposeAsync().ConfigureAwait(false); // first-wins: this later candidate is surplus
        }
    }

    // Wires the built session's transport-lifecycle and inbound-media events onto this peer via the event bridge.
    // The peer supplies the raise delegates (null-conditional invoke of THIS peer's events); TransitionTo, which
    // owns the _sync-guarded connection state, stays here and is passed as a delegate.
    // Commits a completed exchange as one atomic step, so nothing can observe a peer that is Stable but has no
    // session (or the reverse). On a renegotiation the session is the one already live and only the inventory and
    // the descriptions move; the hand-over flag is already true and re-asserting it is a no-op.
    private void CommitSession(
        BundledMediaSession? session, SdpSessionDescription remote, string remoteSdp, string localSdp)
    {
        lock (_sync)
        {
            _session = session;
            // The transport now owns the pre-bound socket (if a session was built); DisposeAsync must not
            // dispose it again.
            _mediaSocket.MarkHandedOver(session is not null);
            // Retain the remote track identity (a=msid) so the receiver can group inbound tracks by the
            // remote MediaStream (the W3C RTCTrackEvent.streams semantics).
            _remoteInventory = WebRtcRemoteMediaInventory.FromRemoteDescription(remote);
            _negotiation.SettleStable(remoteSdp, localSdp);
        }
    }

    // Runs once a newly built session has been published: its events are wired first, then it is handed to the
    // trickle receiver, which drains the candidates buffered before it existed and routes later ones live
    // (RFC 8838) under its own gate so none is lost.
    private void OnSessionBuilt(BundledMediaSession session)
    {
        WireSession(session);
        // Adopt a retained stream relay into the freshly built session (the offerer path — its session did not
        // exist when the candidate was gathered). A no-op for the answerer (already adopted at gather) and when
        // no stream relay was gathered.
        _streamRelay.AdoptInto(session);
        _trickleIce.AttachSession(session);
    }

    private void WireSession(BundledMediaSession session)
    {
        _sessionEvents.WireSession(
            session,
            TransitionTo,
            (payload, rtpTimestamp) => AudioReceived?.Invoke(payload, rtpTimestamp),
            (mid, payload, rtpTimestamp) => AudioTrackFrameReceived?.Invoke(mid, payload, rtpTimestamp),
            frame => VideoFrameReceived?.Invoke(frame),
            (mid, frame) => VideoTrackFrameReceived?.Invoke(mid, frame),
            (mid, rid, frame) => VideoLayerFrameReceived?.Invoke(mid, rid, frame),
            () => VideoKeyFrameRequested?.Invoke(),
            (mid, request) => VideoTrackKeyFrameRequested?.Invoke(mid, request),
            (toneCode, durationMs) => DtmfReceived?.Invoke(toneCode, durationMs));
        // Fan the session's transport-cc recommended-bitrate revisions onto the peer's reactive surface (4.7.0).
        _congestion.WireSession(session, (bps, quality) => RecommendedBitrateChanged?.Invoke(bps, quality));
    }

    // The SDP media options for the offer/answer, assembled by WebRtcSdpOptionsBuilder (extracted to keep this
    // file under the size limit): stable mode always uses numeric MIDs and append-only runtime order;
    // compatibility mode keeps the 1+1 semantic path until an added track selects its historic grouped numeric
    // path. The self-locking WebRtcAddedTrackSet snapshots both track kinds and their insertion order.
    private SdpMediaOptions MediaOptions(IPEndPoint local)
        => WebRtcSdpOptionsBuilder.Build(
            local,
            _hostCandidateProvider.GetHostEndPoints(local),
            _options,
            // Not _options.Ice: an ICE restart rotates the local credentials on a live peer (RFC 8445 §9.1.1.1),
            // and every description built after one must advertise the rotated pair.
            _renegotiator.LocalIceParameters,
            _addedTracks.SnapshotAudio(),
            _addedTracks.SnapshotVideo(),
            _mediaStreamId,
            _audioTrackId,
            _videoTrackId);

    private void TransitionTo(WebRtcConnectionState next) => _connectionState.TransitionTo(next);

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
            orphanSocket = _mediaSocket.TakeOrphan();
            // Signalling terminates at Closed (RFC 8829 §4.1.3), idempotent across a double dispose. The event
            // is fired below, outside the lock (K3).
            signalingClosed = _negotiation.Close();
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
        // Dispose a stream relay that no session ever adopted (an offerer whose session was never built); an
        // adopted one was just disposed by the session above.
        await _streamRelay.DisposeAsync().ConfigureAwait(false);
        orphanSocket?.Dispose();
        _mdnsLifetime.Dispose();
    }
}
