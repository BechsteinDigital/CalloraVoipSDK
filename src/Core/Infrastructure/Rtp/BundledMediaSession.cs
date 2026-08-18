using System.Net;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Assembles a full BUNDLE media session (ADR-011 B5, RFC 8843) from negotiated parameters: one shared
/// <see cref="BundledMediaTransport"/> (socket, B3-1) keyed by one <see cref="BundledDtlsKeying"/>
/// (DTLS-SRTP, B3-2) and kept alive by one <see cref="BundledIceControl"/> (ICE/consent, B3-3), carrying
/// one audio track and zero or more video tracks (<see cref="BundledVideoTrack"/>, B4 — P2b: N video
/// m-lines such as a camera plus a screen-share) over the inbound and outbound pipelines (B2c-in). This is
/// the object that ties the transport slices into one startable unit — the internal composition a
/// signalling-neutral WebRTC facade drives, or that the SDP negotiator builds from a BUNDLE-negotiated
/// offer/answer.
/// <para>
/// Each video track rides its own MID on its own bundle-wide-distinct SSRC(s); inbound packets are routed to
/// the owning track by MID (the router demultiplexes by the MID header extension, RFC 9143, when tracks share
/// a payload type), and per-SSRC SRTP (ADR-011) keeps two simultaneous video streams encrypting/decrypting
/// independently — so two same-codec video tracks never cross-talk. The mid-less send/receive members address
/// the primary (first) video track for backward compatibility with the pre-P2b 1-audio-1-video path; the
/// mid-carrying members (P2b) address a specific track. The public add-a-track surface is P2c.
/// </para>
/// </summary>
internal sealed class BundledMediaSession : IAsyncDisposable
{
    private readonly BundledMediaTransport _transport;
    private readonly BundledOutboundPipeline _outbound;
    private readonly BundledInboundPipeline _inbound;
    // The MID→sink router, retained so a video track added mid-call (AddVideoTrack, P3b) can extend the demux
    // boundary (AddKnownMid) and register its inbound sink live, and SetVideoTrackInactive can unregister it —
    // both without touching the shared transport/DTLS/ICE. Its registry is a ConcurrentDictionary (K3).
    private readonly BundledTrackRouter _router;
    private readonly BundledDtlsKeying _dtls;
    private readonly BundledIceControl _ice;
    private readonly BundledRtcpReporter _rtcpReporter;
    private readonly BundledInboundReceptionStats _receptionStats;
    private readonly BundledOutboundQualityTracker _outboundQuality;
    private readonly IRtcpPacketCodec _rtcpCodec;
    // Decodes each inbound RTCP compound and fans it out to reception stats, outbound quality, the per-track
    // feedback path, and the congestion plane (extracted to keep this session under the size limit). Built after
    // the video set and congestion plane exist; only ever invoked from the receive loop, which starts in StartAsync
    // (after construction), so it is always assigned by dispatch time.
    private readonly BundledInboundRtcpDispatcher _rtcpDispatcher;
    // The bundle's video tracks (P2b: N video m-lines, RFC 8843 §9), keyed by MID. Empty for an audio-only
    // bundle; the first is the primary, addressed by the mid-less send/receive facade for backward compatibility.
    private readonly BundledVideoTrackSet _video;
    // The bundle's ADDITIONAL inbound audio tracks (4.7.0: N audio m-lines, RFC 8843 §9), keyed by MID. Empty for
    // a single-audio bundle. The PRIMARY audio (options.Audio, the transport anchor) is NOT in this set — it keeps
    // the mid-less AudioReceived event; these extra receive-only sinks surface on the mid-tagged event instead.
    private readonly BundledAudioTrackSet _audioTracks;

    // Transport-wide congestion control (transport-cc), one plane for the WHOLE bundle because
    // transport-cc numbers the transport, not a stream. Null unless the a=extmap was negotiated. See
    // BundledCongestionPlane — the sender-side controller (recommended bitrate) and the receive-side feedback
    // sender, with their own lifetime token; OnControlPacketReceived fans decoded feedback into it.
    private readonly BundledCongestionPlane? _congestion;

    private readonly string _audioMid;
    private readonly uint _audioSsrc;
    private readonly bool _audioSendEnabled;
    // Which stream every SSRC belongs to: the outbound SSRC → (MID, kind) map behind the per-stream quality
    // snapshot, plus the inbound clock/kind/MID registration. Follows live track mutation (#161 P2-11).
    private readonly BundledStreamAttribution _attribution;
    // RFC 4733 telephone-event (DTMF): the negotiated event payload type on the audio track (null when the
    // peer did not offer/accept telephone-event — DTMF sends then throw) and the event clock rate used to
    // convert durations to/from RTP units (RFC 4733 §2.1: it shares the audio stream's timestamp clock).
    private readonly int? _telephoneEventPayloadType;
    private readonly int _telephoneEventClockRate;
    private readonly ILogger<BundledMediaSession> _logger;
    // Retained for the live add-a-track path (AddVideoTrack, P3b): the negotiated options carry the header-
    // extension ids, initial sequence/timestamp, and reorder depth the composition helper needs to build a new
    // BundledVideoTrack identically to the ctor path, plus the factory to wire its outbound sender + events.
    private readonly BundledMediaSessionOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    // Serialises live structural changes (AddVideoTrack / SetVideoTrackInactive) so two concurrent mutations
    // cannot interleave their known-mid → outbound → track → sink → set registration steps. The receive loop is
    // NOT gated by this — it reads the router/set lock-free; this only orders control-plane mutations against
    // each other. 0 = live, 1 = disposed (set in DisposeAsync so a late add fails fast rather than leaking a track).
    private readonly object _trackMutationGate = new();
    private readonly object _startGate = new();   // latches the one start (#157 P2-7)
    private Task? _startTask;
    private int _disposed;

    // Tracks removed by SetVideoTrackInactive: routing already dropped, but disposed only in DisposeAsync (never
    // live — BundledVideoTrack.Dispose needs in-flight send/receive drained first, HARD-C6). Added under the gate.
    private readonly List<BundledVideoTrack> _deactivatedVideoTracks = [];

    // The live (mid-call) track-mutation engine (4.7.0 renegotiation): adds/deactivates a video or additional audio
    // track on the running bundle. Shares _trackMutationGate with this session (the same lock object, not a new one)
    // so control-plane mutations stay serialised against each other; extracted to keep this file under the size limit.
    private readonly BundledMediaSessionTrackMutation _trackMutation;

    // Inbound-media event wiring collaborator (per-video-track frame/key-frame subscriptions + the guarded
    // additional-audio raise), extracted to keep this file under the size limit (R3). The session's inbound events
    // stay on the session; this holds only the subscription plumbing both the ctor loop and AddVideoTrack share.
    private readonly BundledMediaSessionInboundEventWiring _inboundEventWiring;

    // Every outbound SSRC this bundle has issued (RFC 3550 §8.1): seeded from the ctor tracks and extended on
    // AddVideoTrack under _trackMutationGate. A deactivated track's SSRCs are RETIRED, not released — under one
    // SRTP key an SSRC must never be issued twice (#161 P2-12) — so this is the set a renegotiation allocates
    // around. MID-keyed internally, since BundledVideoTrack does not expose its SSRCs.
    private readonly BundledOutboundSsrcTracker _outboundSsrcs;

    // RFC 4733 inbound DTMF reassembly (extracted to RtpInboundDtmfReassembler). Driven only by
    // RaiseAudioReceived, which runs solely on the single shared receive loop, so the reassembler needs no
    // synchronization. Null when the peer did not negotiate telephone-event (no reassembly path).
    private readonly RtpInboundDtmfReassembler? _dtmfReassembler;
    // The ICE view currently in force. Mutable because an ICE restart (RFC 8445 §9) rotates the credentials and
    // replaces the agent; volatile because a renegotiator reads it off the signalling thread while a restart
    // publishes from another. Only the credentials are read back — the agent owns the rest.
    private volatile IceMediaParameters _iceParameters;

    // The bundle's TURN relay data path (RFC 8656): the wiring claim, the allocation keepalive, the one-shot
    // switch onto ChannelData when a relay pair wins ICE, and its teardown order. Extracted so the session does
    // not carry that whole lifecycle inline; a session without a relay allocation just holds an inert one.
    private readonly BundledRelayDataPath _relay;

    /// <summary>
    /// Raised with each decrypted inbound audio RTP packet on the <em>primary</em> audio track (the transport
    /// anchor). Backward-compatible with the pre-4.7.0 single-audio path: it never fires for an additional audio
    /// m-line — use <see cref="AudioTrackFrameReceived"/> for those.
    /// </summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>
    /// Raised with each decrypted inbound audio RTP packet on an <em>additional</em> audio m-line (4.7.0: N audio
    /// tracks — the SFU pattern of one audio stream per remote participant), tagged with its MID. Fires only for
    /// the additional receive-only tracks, never for the primary anchor (which stays on the mid-less
    /// <see cref="AudioReceived"/>). Runs on the shared receive loop.
    /// </summary>
    public event Action<string, RtpPacket>? AudioTrackFrameReceived;

    /// <summary>
    /// Raised once per fully received inbound RFC 4733 telephone-event (DTMF), carrying the tone code (0–15)
    /// and the reassembled tone duration in milliseconds. Fired on the shared receive loop from the event's
    /// end-of-event packet; telephone-event packets are consumed here and never surfaced on
    /// <see cref="AudioReceived"/>.
    /// </summary>
    public event Action<byte, int>? DtmfReceived;

    /// <summary>
    /// Raised with each reassembled inbound video frame on the <em>primary</em> video track. Backward-compatible
    /// with the pre-P2b single-video path: with exactly one video track this fires for that track's frames; with
    /// several it fires only for the primary (first) track. Use <see cref="VideoTrackFrameReceived"/> to receive
    /// every track's frames tagged with its MID.
    /// </summary>
    public event Action<InboundVideoFrame>? VideoFrameReceived;

    /// <summary>
    /// Raised with each reassembled inbound video frame on any video track (P2b), tagged with the MID of the
    /// track it arrived on. Fires for every video track — the way to tell N video tracks apart on the inbound
    /// path. Runs on the shared receive loop.
    /// </summary>
    public event Action<string, InboundVideoFrame>? VideoTrackFrameReceived;

    /// <summary>
    /// Raised with each reassembled inbound video frame that belongs to a specific simulcast layer (RFC 8853):
    /// the m-line MID, the layer's <c>a=rid</c> (RFC 8852), and the frame — including the Dependency Descriptor's
    /// layer information where the peer negotiated it (#225). The Core recv-side surface for SFU forwarding — one
    /// event per demultiplexed encoding. Fires <em>only</em> for RID-tagged layers, never for the primary/default
    /// (RID-less) stream, which continues to surface on <see cref="VideoFrameReceived"/> /
    /// <see cref="VideoTrackFrameReceived"/>. Runs on the shared receive loop.
    /// </summary>
    internal event Action<string, string, InboundVideoFrame>? VideoLayerFrameReceived;

    /// <summary>
    /// Raised when the peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104) on the video track;
    /// the app should encode and send a key frame.
    /// </summary>
    public event Action? VideoKeyFrameRequested;

    /// <summary>Raised when the shared DTLS handshake fails — media stays blocked (fail closed).</summary>
    public event Action? HandshakeFailed;

    /// <summary>
    /// Raised when the peer closes the shared DTLS association after key export (close_notify or fatal
    /// alert) — the keying channel the peer considers closed must not keep carrying media (#190).
    /// </summary>
    public event Action? PeerClosed;

    /// <summary>Raised when the shared DTLS handshake installs the SRTP keys and media can flow.</summary>
    public event Action? Connected;

    /// <summary>Raised once when RFC 7675 ICE consent is lost for the shared 5-tuple.</summary>
    public event Action? MediaConsentLost;

    /// <summary>Raised on a transient consent miss still inside the consent window (RFC 7675).</summary>
    public event Action? MediaConnectivityDegraded;

    /// <summary>Raised when a consent check is answered again after a degrade.</summary>
    public event Action? MediaConnectivityRecovered;

    public BundledMediaSession(
        BundledMediaSessionOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Audio);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _loggerFactory = loggerFactory;
        _audioMid = options.Audio.Mid;
        _audioSendEnabled = options.AudioSendEnabled;
        _telephoneEventPayloadType =
            options.Audio.TelephoneEventPayloadType is >= 0 and <= 127 ? options.Audio.TelephoneEventPayloadType : null;
        _telephoneEventClockRate = options.Audio.TelephoneEventClockRate > 0 ? options.Audio.TelephoneEventClockRate : 8000;
        _logger = loggerFactory.CreateLogger<BundledMediaSession>();
        // Inbound-media event wiring (per-video-track frame/key-frame subscriptions + guarded additional-audio raise)
        // lives in a collaborator to keep this file under the size limit (R3); the events stay on this session (they
        // can only be invoked from here), so it is handed thin raise delegates that null-conditionally invoke each.
        _inboundEventWiring = new BundledMediaSessionInboundEventWiring(
            frame => VideoFrameReceived?.Invoke(frame),
            (mid, frame) => VideoTrackFrameReceived?.Invoke(mid, frame),
            (mid, rid, frame) => VideoLayerFrameReceived?.Invoke(mid, rid, frame),
            () => VideoKeyFrameRequested?.Invoke(),
            (mid, packet) => AudioTrackFrameReceived?.Invoke(mid, packet),
            _logger);
        // Inbound DTMF reassembler only when telephone-event was negotiated (RFC 4733): it fires DtmfReceived on a
        // completed tone. Driven solely by the receive loop (via RaiseAudioReceived), so it needs no locking.
        _dtmfReassembler = _telephoneEventPayloadType is not null
            ? new RtpInboundDtmfReassembler(_telephoneEventClockRate, (tone, ms) => DtmfReceived?.Invoke(tone, ms), _logger)
            : null;

        // Inbound: demux the shared socket by the negotiated m-lines' payload types, route each MID.
        var payloadTypesByMid = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
        {
            [options.Audio.Mid] = new[] { (int)options.Audio.PayloadType },
        };
        // One entry per video m-line (P2b). A payload type shared across video tracks (two same-codec streams
        // both use, e.g., PT 96) is dropped from the PT→MID demux map by the factory below, so those packets are
        // demultiplexed by MID header extension (RFC 9143) instead — never guessed by an ambiguous PT.
        foreach (var videoConfig in options.VideoTracks)
        {
            if (!payloadTypesByMid.TryAdd(videoConfig.Mid, new[] { (int)videoConfig.PayloadType }))
                throw new ArgumentException(
                    $"Duplicate video MID '{videoConfig.Mid}' in the bundle options.", nameof(options));
        }
        // One entry per additional inbound audio m-line (4.7.0). As for video, a PT shared across audio tracks is
        // dropped from the demux map by the factory below and those packets route by MID header extension
        // (RFC 9143) — never an ambiguous PT. A MID colliding with the primary audio or a video m-line is rejected.
        foreach (var audioConfig in options.AdditionalAudioTracks)
        {
            if (!payloadTypesByMid.TryAdd(audioConfig.Mid, new[] { (int)audioConfig.PayloadType }))
                throw new ArgumentException(
                    $"Duplicate audio MID '{audioConfig.Mid}' in the bundle options.", nameof(options));
        }

        var router = new BundledTrackRouter(
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, payloadTypesByMid, options.RidExtensionId));
        _router = router;
        router.RegisterTrack(options.Audio.Mid, RaiseAudioReceived);

        // Per-SSRC inbound reception statistics (RFC 3550 §6.4.1) feed the periodic RTCP report blocks: the
        // inbound pipeline records each decoded RTP packet, and inbound SRs feed LSR/DLSR (subscribed below).
        // The negotiated clock/kind is applied per inbound source by matching the first packet's payload type
        // (the inbound SSRC is the remote's choice), so audio gets its exact §A.8 clock and video gets 90 kHz
        // regardless of arrival order, and each source is attributed to its track (CF-004f).
        _receptionStats = new BundledInboundReceptionStats(clockByPayloadType: BundledMediaSessionComposition.BuildInboundClockMap(options));
        // Consumes the reception blocks the peer returns about our outbound streams to derive RTT and the loss
        // the peer sees (RFC 3550 §6.4.1): fed by the reporter's SR send instants and by inbound RR/SR blocks.
        _outboundQuality = new BundledOutboundQualityTracker();
        _rtcpCodec = new RtcpPacketCodec();

        _inbound = new BundledInboundPipeline(
            router, new RtpPacketCodec(), loggerFactory.CreateLogger<BundledInboundPipeline>(), _receptionStats);
        // Inbound Sender Reports carry the LSR the peer needs echoed back: decode each decrypted compound and
        // record every SR's middle-32 NTP bits + arrival time per sender SSRC (RFC 3550 §6.4.1).
        _inbound.ControlPacketReceived += OnControlPacketReceived;

        _transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = options.LocalEndPoint, RemoteEndPoint = options.RemoteEndPoint },
            _inbound, loggerFactory.CreateLogger<BundledMediaTransport>(), options.PreBoundSocket);

        // A relay ICE local candidate rides the same shared socket. Now that the socket exists, the injected
        // (TURN-aware) factory builds the indication channel + control transactor + relay send path from the
        // transport's targeted send; the transport unwraps relayed inbound datagrams and feeds control responses
        // (SetIndicationRelay), and the relay send path becomes the ICE agent's relay candidate below. Null
        // (no gathered allocation) leaves the transport direct-only.
        // Unframed send: the relay control stack (control transactions + Send indications) is addressed to the
        // relay server itself and must reach it raw in both modes — never framed as ChannelData once the
        // transport enters relay mode.
        var relayBinding = options.RelayIceBindingFactory?.Invoke(_transport.SendUnframedAsync);
        if (relayBinding is not null)
            _transport.SetIndicationRelay(relayBinding.Indication, relayBinding.OnControl);

        // Outbound: a per-track sender for each m-line, stamping its MID (and, when negotiated, the one
        // transport-wide-cc sequence the pipeline advances across all tracks).
        _outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), _transport, loggerFactory.CreateLogger<BundledOutboundPipeline>(),
            stampsTransportCc: options.TransportWideCcExtensionId is not null);
        _outbound.RegisterTrack(options.Audio.Mid, BundledMediaSessionComposition.BuildOutboundTrack(options, options.Audio));

        // The additional inbound audio tracks (4.7.0) — each a bare receive sink plus a symmetric outbound sender —
        // are wired by the collaborator (kept out of this ctor for the size limit): it registers each MID's router
        // sink (raising the mid-tagged AudioTrackFrameReceived) and its outbound sender. The demux boundary was
        // extended above; the primary anchor is untouched. Empty list → empty set (byte-identical single-audio path).
        _audioTracks = options.AdditionalAudioTracks.Count > 0
            ? new BundledAudioTrackSet(options, router, _outbound, RaiseAudioTrackReceived, _logger)
            : new BundledAudioTrackSet(_outbound, _logger);

        // One BundledVideoTrack per negotiated video m-line (P2b: N video tracks). Each registers its own
        // outbound sender(s) and its own inbound router sink on its MID; per-SSRC SRTP keeps them independent.
        var builtVideo = new List<(string Mid, BundledVideoTrack Track)>(options.VideoTracks.Count);
        foreach (var video in options.VideoTracks)
        {
            var track = BundledMediaSessionComposition.BuildVideoTrack(options, video, _outbound, loggerFactory);
            var mid = video.Mid;
            var isPrimary = builtVideo.Count == 0;
            _inboundEventWiring.WireVideoTrackEvents(mid, track, isPrimary);
            router.RegisterTrack(mid, track.OnRtpPacket);
            builtVideo.Add((mid, track));
        }

        _video = builtVideo.Count > 0 ? new BundledVideoTrackSet(builtVideo) : new BundledVideoTrackSet();

        // Transport-wide congestion control (transport-cc), one plane per bundle. Only when the
        // a=extmap was negotiated (so the transport actually stamps a transport-wide sequence) — otherwise the
        // plane stays off. See BundledCongestionPlane: it wires the sender-side controller to PacketSent and the
        // receive-side feedback sender to inbound RTP; OnControlPacketReceived fans decoded feedback into it.
        _congestion = BundledMediaSessionComposition.BuildCongestionPlane(
            options, _outbound, _inbound, _rtcpCodec, loggerFactory);

        // Inbound RTCP decode + fan-out (RFC 3550 §6.4.1 / RFC 4585 / transport-cc): built now that the video set and
        // congestion plane exist. Invoked only from the receive loop (subscribed on _inbound above), which starts
        // in StartAsync, so this field is always assigned before the first dispatch.
        _rtcpDispatcher = new BundledInboundRtcpDispatcher(
            _rtcpCodec, _receptionStats, _outboundQuality, _video, _congestion, _logger);

        // One shared DTLS association keys every track; one shared ICE agent keeps the group alive.
        _dtls = new BundledDtlsKeying(
            options.DtlsIsClient, options.RemoteEndPoint, options.RemoteFingerprint,
            handshaker, certificate, _inbound, _outbound, _transport,
            onHandshakeFailed: () => HandshakeFailed?.Invoke(), loggerFactory,
            onKeysInstalled: () => Connected?.Invoke(),
            onPeerClosed: () => PeerClosed?.Invoke());

        _ice = new BundledIceControl(
            options.Ice, _inbound, _transport.SendToAsync, loggerFactory,
            onConsentLost: () => MediaConsentLost?.Invoke(),
            onConnectivityDegraded: () => MediaConnectivityDegraded?.Invoke(),
            onConnectivityRecovered: () => MediaConnectivityRecovered?.Invoke(),
            // A nominated ICE pair (RFC 8445 §8) becomes the transport's send target AND the DTLS remote,
            // so the DTLS handshake's inbound source filter follows the connectivity-checked pair.
            onPairNominated: OnPairNominated,
            // The relay send path (when a TURN allocation was gathered) becomes the ICE agent's relay local
            // candidate — checked alongside the direct one, direct-preferred by pair priority.
            relaySend: relayBinding?.RelaySend,
            // A nominated relay pair additionally switches the transport onto the relay data path (ChannelBind).
            onRelayPairNominated: OnRelayPairNominated);

        // Periodic RTCP Sender Reports for the active outbound streams (RFC 3550 §6.4): reads the outbound
        // pipeline's per-SSRC SR counters and sends over its fail-closed SRTCP send path. The CNAME mirrors the
        // SIP-path monitor so both report the same canonical name. Started in StartAsync (early ticks are
        // suppressed until DTLS installs the outbound SRTCP key); disposed before the transport it rides.
        _rtcpReporter = new BundledRtcpReporter(
            _outbound.SnapshotSenderReports,
            _receptionStats.SnapshotReportBlocks,
            options.Audio.Ssrc,
            _outbound.SendRtcpAsync,
            _rtcpCodec,
            // Opaque per-session CNAME (RFC 7022) — never the machine name (privacy/correlation); overridable.
            options.Cname ?? RtcpCname.NewOpaque(),
            loggerFactory,
            // Record each emitted SR's LSR + send instant so a peer's echoed report yields RTT (RFC 3550 §6.4.1).
            onSenderReportSent: _outboundQuality.RecordLocalSenderReport);

        _audioSsrc = options.Audio.Ssrc;
        // Seed the outbound-SSRC bookkeeping (RFC 3550 §8.1) from the ctor tracks so OutboundSsrcs reflects the
        // SSRCs issued from the start — the seed a renegotiation allocates around.
        _outboundSsrcs = new BundledOutboundSsrcTracker(options.Audio.Ssrc, _logger);
        foreach (var video in options.VideoTracks)
            _outboundSsrcs.Add(video.Mid, video);
        _attribution = new BundledStreamAttribution(BundledMediaSessionComposition.BuildOutboundStreamIdentity(options), _receptionStats);

        // The live track-mutation engine (4.7.0 renegotiation). It shares _trackMutationGate (the same object, so
        // add/remove stays serialised) and reads _disposed under it via the passed predicate so a late add fails
        // fast. The inbound-event wiring collaborator's WireVideoTrackEvents / RaiseAudioTrackReceivedGuarded are
        // handed in so the wiring semantics (incl. the K3 additional-audio subscriber guard) stay identical to the
        // ctor path.
        _trackMutation = new BundledMediaSessionTrackMutation(
            _trackMutationGate,
            () => Volatile.Read(ref _disposed) != 0,
            _router, _outbound, _video, _audioTracks, _outboundSsrcs, _deactivatedVideoTracks,
            options, loggerFactory, _audioMid,
            _inboundEventWiring.WireVideoTrackEvents, _inboundEventWiring.RaiseAudioTrackReceivedGuarded,
            _attribution);

        // The relay data path: wired here when the offerer already gathered a TURN allocation, otherwise open for
        // a later AdoptRelay (the answerer, which gathers after the session exists).
        _relay = new BundledRelayDataPath(_transport, relayBinding, _logger);

        // The ICE view the agent above was built with; RestartIceAsync replaces both together.
        _iceParameters = options.Ice;
    }

    // Dispatches inbound audio to subscribers on the receive loop; a throwing subscriber must not tear
    // down the shared receive loop (the video path is guarded the same way inside BundledVideoTrack).
    // RFC 4733 telephone-event packets share the audio MID (same demux key) but are DTMF, not audio: they
    // are reassembled and surfaced on DtmfReceived, never forwarded to AudioReceived.
    private void RaiseAudioReceived(RtpPacket packet)
    {
        if (_dtmfReassembler is not null
            && _telephoneEventPayloadType is { } telephoneEventPayloadType
            && packet.PayloadType == telephoneEventPayloadType)
        {
            _dtmfReassembler.Handle(packet);
            return;
        }
        _dtmfReassembler?.PollTimeout(); // closes a tone whose end-of-event packet was lost (#161 P3-16)

        try
        {
            AudioReceived?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled audio AudioReceived handler.");
        }
    }

    // Raises the mid-tagged inbound-audio event for an additional audio m-line (4.7.0). Passed to the audio-track
    // collaborator, which guards it against a throwing subscriber (K3). DTMF is not reassembled here — RFC 4733
    // telephone-event stays on the primary audio track (the send/DTMF facade addresses only the primary).
    private void RaiseAudioTrackReceived(string mid, RtpPacket packet) => AudioTrackFrameReceived?.Invoke(mid, packet);

    /// <summary>
    /// Test seam: injects one inbound audio-MID RTP packet straight into the audio dispatch path
    /// (<see cref="RaiseAudioReceived"/>), bypassing the socket/SRTP so the telephone-event reassembly and
    /// audio/DTMF split can be driven deterministically without a live transport. Not part of the media path.
    /// </summary>
    internal void InjectInboundAudioForTest(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RaiseAudioReceived(packet);
    }

    // Decodes an inbound decrypted RTCP compound and fans it out (RFC 3550 §6.4.1 / RFC 4585 / transport-cc) via the
    // extracted dispatcher. Runs on the receive loop; the dispatcher swallows a malformed compound with a log so it
    // cannot tear the loop down.
    private void OnControlPacketReceived(byte[] rtcp) => _rtcpDispatcher.Dispatch(rtcp);

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>
    /// The remote peer's ICE username fragment currently in force (RFC 8839), or null when the exchange carried no
    /// remote ICE credentials. A renegotiator compares it against a re-offer's ufrag to detect an ICE restart
    /// (RFC 8829 §5.3.1). Reflects the running agent, so it follows a <see cref="RestartIceAsync"/> — reading it
    /// from the construction-time options would report a restart against every later re-offer.
    /// </summary>
    public string? RemoteIceUfrag => _iceParameters.RemoteIceUfrag;

    /// <summary>
    /// The remote peer's ICE password currently in force (RFC 8839), or null when the exchange carried no remote
    /// ICE credentials. Paired with <see cref="RemoteIceUfrag"/> because a restart may rotate either
    /// (RFC 8445 §9.1.1.1).
    /// </summary>
    public string? RemoteIcePwd => _iceParameters.RemoteIcePwd;

    /// <summary>
    /// Restarts ICE on the running session (RFC 8445 §9, RFC 8839 §5.4): the agent is replaced with one built from
    /// <paramref name="parameters"/> — rotated credentials, the re-offer's remote candidates, a fresh check list —
    /// and connectivity checks run again over the same socket.
    /// <para>
    /// Nothing above ICE is rebuilt: the transport, its socket, the DTLS association and every per-SSRC SRTP
    /// context survive, so tracks keep their keys and their sequence/index space. That is what a restart means to a
    /// peer — the path is re-selected, the session is not renegotiated.
    /// </para>
    /// <para>
    /// The transport's send target is deliberately left alone. RFC 8445 §9 keeps media on the previously selected
    /// pair until a new one is selected, so a peer that rotated its credentials because it changed networks keeps
    /// receiving on the old path for as long as that path still works, instead of losing media the moment the
    /// re-offer arrives. The new agent re-points the transport itself when it nominates a pair.
    /// </para>
    /// </summary>
    /// <param name="parameters">The new ICE view: rotated credentials, role, remote endpoint and candidates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async Task RestartIceAsync(IceMediaParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _ice.RestartIceAsync(parameters).ConfigureAwait(false);
        // Publish only after the swap succeeded, so a failed restart does not leave the session claiming
        // credentials no agent is answering with — which would make the next re-offer look like no restart at all.
        _iceParameters = parameters;
    }

    /// <summary>The local audio track's synchronisation source.</summary>
    public uint AudioSsrc => _audioSsrc;

    /// <summary>
    /// A snapshot of every outbound synchronisation source this bundle has issued under its current SRTP key
    /// (RFC 3550 §8.1): the audio SSRC, each active track's primary/per-encoding and RTX SSRC(s), and every SSRC
    /// retired with a deactivated track. A renegotiator seeds its SSRC allocation with this, so a mid-call-added
    /// track's SSRCs are distinct from all of them — reusing one would restart a stream's index space under a key
    /// whose per-SSRC SRTP state is still live, sharing a keystream with the retired stream (#161 P2-12).
    /// </summary>
    public IReadOnlySet<uint> OutboundSsrcs => _outboundSsrcs.Snapshot();

    /// <summary>Whether this bundle carries at least one video track.</summary>
    public bool HasVideo => _video.Any;

    /// <summary>The number of video tracks on this bundle (P2b: N video m-lines).</summary>
    public int VideoTrackCount => _video.Count;

    /// <summary>The MID tokens of the video tracks on this bundle, in build order (primary first).</summary>
    public IReadOnlyList<string> VideoMids => _video.Mids;

    /// <summary>Whether this bundle carries at least one additional inbound audio track beyond the primary anchor (4.7.0).</summary>
    public bool HasAdditionalAudio => _audioTracks.Any;

    /// <summary>The number of additional inbound audio tracks on this bundle (excluding the primary anchor).</summary>
    public int AdditionalAudioTrackCount => _audioTracks.Count;

    /// <summary>The MID tokens of the additional inbound audio tracks on this bundle, in negotiated order.</summary>
    public IReadOnlyList<string> AdditionalAudioMids => _audioTracks.Mids;

    /// <summary>
    /// The MID tokens of the active <em>additional</em> audio tracks on this bundle (4.7.0), the audio pendant to
    /// <see cref="VideoMids"/> and the set a renegotiator diffs a re-offer's audio m-lines against. The PRIMARY
    /// audio m-line (the transport anchor) is never in this set — it is never diff'd, added, or deactivated. An
    /// alias of <see cref="AdditionalAudioMids"/> named for the renegotiation diff path.
    /// </summary>
    public IReadOnlyList<string> AudioMids => _audioTracks.Mids;

    /// <summary>
    /// The MID of the PRIMARY audio m-line (the bundle transport anchor). It carries ICE/DTLS and the mid-less
    /// audio path, so it is never one of the additional/diffable audio tracks: a renegotiator must never add,
    /// deactivate, or diff it, and <see cref="SetAudioTrackInactive"/> refuses it (anchor protection).
    /// </summary>
    public string PrimaryAudioMid => _audioMid;

    /// <summary>
    /// Whether outbound audio is sent. False when the negotiated directions do not carry audio from this peer
    /// to the remote (a send-only/inactive remote answer, or a local side that does not send); the audio
    /// m-line still anchors the transport and inbound audio is still received.
    /// </summary>
    public bool AudioSendEnabled => _audioSendEnabled;

    /// <summary>Whether the primary video track sends multiple simulcast encodings (RFC 8853).</summary>
    public bool VideoIsSimulcast => _video.Primary?.IsSimulcast ?? false;

    /// <summary>The primary video track's simulcast <c>a=rid</c> layer ids, or empty when not simulcasting.</summary>
    public IReadOnlyCollection<string> VideoSendRids => _video.Primary?.SendRids ?? [];

    /// <summary>The remote media endpoint the shared transport sends to, or null before one is set.</summary>
    public IPEndPoint? RemoteEndPoint => _transport.RemoteEndPoint;

    /// <summary>
    /// Points the shared transport at a new remote media endpoint (a trickled ICE candidate, RFC 8838).
    /// Thread-safe; the symmetric transport still latches the peer's real source on the next received packet.
    /// </summary>
    public void SetRemoteEndPoint(IPEndPoint remoteEndPoint) => _transport.SetRemoteEndPoint(remoteEndPoint);

    /// <summary>
    /// Adds a trickled remote ICE candidate (RFC 8838) to the connectivity-check list instead of trusting it
    /// by raw priority: the controlling agent checks it and, if it answers and beats the current pair,
    /// nominates it (redirecting the transport send target and DTLS). No-op on a controlled agent or without ICE.
    /// </summary>
    /// <param name="remoteEndPoint">The candidate's transport address.</param>
    /// <param name="priority">The candidate's ICE priority (RFC 8445 §5.1.2.1), used to order checks.</param>
    public void AddRemoteCandidate(IPEndPoint remoteEndPoint, long priority)
        => _ice.AddRemoteCandidate(new IceRemoteCandidate(remoteEndPoint, priority));

    /// <summary>
    /// Adopts a relay ICE local candidate after the session was already built — the answerer path, whose TURN
    /// allocation only finished gathering post-construction (the offerer wires its relay at construction via
    /// <see cref="BundledMediaSessionOptions.RelayIceBindingFactory"/>). Invokes
    /// <paramref name="relayIceBindingFactory"/> with the shared transport's targeted send to build the relay
    /// wiring, routes inbound relayed Data indications and the relay server's control responses into the
    /// transport (<see cref="BundledMediaTransport.SetIndicationRelay"/>), and hands the ICE agent the relay
    /// send path as a second local candidate — checked alongside the direct one, direct-preferred by pair
    /// priority (RFC 8445 §6.1.2.3). Idempotent: a no-op once the relay path is already wired (at construction
    /// or a prior adoption), when the factory yields no binding, or on a controlled agent (no ICE driver).
    /// Call after the shared socket exists (post-construction) and before <see cref="StartAsync"/>; the check
    /// list picks the relay pair up live if the loop is already running.
    /// </summary>
    /// <param name="relayIceBindingFactory">Builds the relay binding from the transport's targeted send.</param>
    public void AdoptRelay(RelayIceBindingFactory relayIceBindingFactory)
    {
        // The relay data path owns the transport-side wiring, the retained binding and the keepalive; it hands
        // back the adopted binding (null when nothing was adopted) so the ICE half stays here.
        if (_relay.TryAdopt(relayIceBindingFactory) is not { } binding)
            return;

        // Hand the ICE agent both the relay send path and the per-peer permission installer: a controlled
        // (answerer) agent uses the installer to proactively permission the offerer's remote-candidate IPs
        // (RFC 8656 §9) so their inbound relay checks reach it rather than being dropped by the TURN server.
        _ice.AddRelayLocalCandidate(binding.RelaySend, binding.EnsurePermission);
    }

    /// <summary>
    /// Adds a video track to this bundle <em>live</em> (P3b): after construction, while the receive loop runs
    /// and media flows on the existing tracks, without touching the shared transport, DTLS association, ICE
    /// agent, or SRTP context — and without interrupting any existing track. The new track rides its own MID on
    /// its own bundle-wide-distinct SSRC(s) (which the caller allocates in <paramref name="video"/>), and after
    /// this call is sendable via <see cref="SendVideoTrackFrameAsync(string, System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
    /// and surfaces inbound frames on <see cref="VideoTrackFrameReceived"/> (never on the mid-less
    /// <see cref="VideoFrameReceived"/>, which stays pinned to the construction-time primary).
    /// <para>
    /// Registration order is deliberate and race-free against the single-consumer receive loop: the demux
    /// boundary is extended first (so inbound packets for the new MID are no longer rejected as unknown), then
    /// the outbound sender, then the track and its inbound sink last. During the window after the MID is known
    /// but before the sink exists, a packet for the new MID is cleanly dropped and counted by the router — never
    /// mis-delivered and never a crash. Inbound demultiplexes by the MID header extension (RFC 9143); the
    /// construction-time payload-type→MID map is not extended, so this requires the peer to stamp the MID
    /// extension on the new track's packets (always the case for a renegotiated WebRTC m-line, RFC 8829).
    /// </para>
    /// This is the session-level half of a mid-call track addition; the SDP-renegotiation wiring that computes the
    /// track diff from a <c>SetRemoteDescription</c> re-offer and drives this lives in <c>WebRtcRenegotiator</c>.
    /// </summary>
    /// <param name="video">The new video m-line's configuration — its MID, codec, payload type, and its own
    /// distinct SSRC(s) (plus optional RTX / simulcast encodings), exactly as a ctor-time video track.</param>
    /// <exception cref="ArgumentNullException"><paramref name="video"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="video"/> has no MID or names no codec.</exception>
    /// <exception cref="InvalidOperationException">A video track with that MID already exists, or the session is disposed.</exception>
    public void AddVideoTrack(BundledTrackConfig video) => _trackMutation.AddVideoTrack(video);

    /// <summary>
    /// Deactivates the video track identified by <paramref name="mid"/> <em>live</em> (P3b): stops its inbound
    /// dispatch and outbound sending and releases its send lock / in-flight feedback, without tearing down the
    /// shared transport, DTLS, ICE, or SRTP context — every other track (and the transport itself) keeps
    /// flowing uninterrupted. Idempotent: a no-op when no video track with that MID is registered.
    /// <para>
    /// Removal order mirrors the add: the inbound sink is unregistered first (inbound stops), then the outbound
    /// sender(s) (outbound stops), then the track is removed from the set and disposed. The demux boundary
    /// keeps the MID known — an unknown MID is a security-relevant demux decision, and re-accepting a MID we
    /// already negotiated costs nothing while a stray late packet for it is harmlessly dropped once its sink is
    /// gone. The construction-time primary is not special-cased away here; removing it is the caller's choice.
    /// </para>
    /// </summary>
    /// <param name="mid">The MID of the video track to deactivate.</param>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is <see langword="null"/> or empty.</exception>
    public void SetVideoTrackInactive(string mid) => _trackMutation.SetVideoTrackInactive(mid);

    /// <summary>
    /// Adds an additional inbound/outbound audio track to this bundle <em>live</em> (4.7.0 renegotiation): after
    /// construction, while the receive loop runs and media flows on the existing tracks, without touching the shared
    /// transport, DTLS association, ICE agent, or SRTP context — and without interrupting any existing track
    /// (including the primary anchor). The new track rides its own MID on its own bundle-wide-distinct SSRC (which
    /// the caller allocates in <paramref name="audio"/>) and surfaces inbound frames on
    /// <see cref="AudioTrackFrameReceived"/> (never on the mid-less <see cref="AudioReceived"/>, which stays pinned
    /// to the primary anchor). The exact audio pendant to <see cref="AddVideoTrack"/>: same race-free
    /// register-order against the single-consumer receive loop (extend the demux boundary first, then the outbound
    /// sender, then the inbound sink last), and the same MID-header-extension demux (RFC 9143).
    /// </summary>
    /// <param name="audio">The new audio m-line's configuration — its MID, codec, payload type, and its own distinct
    /// SSRC — exactly as a ctor-time additional audio track.</param>
    /// <exception cref="ArgumentNullException"><paramref name="audio"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="audio"/> has no MID.</exception>
    /// <exception cref="InvalidOperationException">
    /// The MID is the primary anchor's MID (never diffable), an audio/video track with that MID already exists, or
    /// the session is disposed.
    /// </exception>
    public void AddAudioTrack(BundledTrackConfig audio) => _trackMutation.AddAudioTrack(audio);

    /// <summary>
    /// Deactivates the additional audio track identified by <paramref name="mid"/> <em>live</em> (4.7.0
    /// renegotiation): stops its inbound dispatch and outbound sending without tearing down the shared transport,
    /// DTLS, ICE, or SRTP context — every other track (including the primary anchor and the transport itself) keeps
    /// flowing uninterrupted. Idempotent: a no-op when no additional audio track with that MID is registered.
    /// <para>
    /// <b>Anchor protection:</b> deactivating the PRIMARY audio m-line's MID is a no-op — that m-line anchors
    /// ICE/DTLS for the whole bundle and must never be torn from the media path, so a renegotiation that (mistakenly)
    /// targeted it silently changes nothing. Removal order mirrors the add: the inbound sink is unregistered first
    /// (inbound stops), then the outbound sender (outbound stops), then the MID is dropped from the set. The demux
    /// boundary keeps the MID known (a stray late packet is harmlessly dropped once its sink is gone). Audio has no
    /// per-track object, so there is no deferred dispose (unlike video's <see cref="SetVideoTrackInactive"/>).
    /// </para>
    /// </summary>
    /// <param name="mid">The MID of the additional audio track to deactivate.</param>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is <see langword="null"/> or empty.</exception>
    public void SetAudioTrackInactive(string mid) => _trackMutation.SetAudioTrackInactive(mid);

    // A connectivity-checked ICE nomination (RFC 8445 §8) redirects the whole 5-tuple onto the nominated
    // pair: the transport's send target and the DTLS association's inbound source filter both follow it, so
    // the handshake completes against the checked candidate rather than the initial SDP endpoint.
    private void OnPairNominated(IPEndPoint remoteEndPoint)
    {
        // Once the relay data path is committed the transport is bound to the relay peer; a later re-nomination
        // (e.g. a direct path that only recovered after relay won) must not re-point the transport, or inbound
        // ChannelData — unwrapped and attributed to _remoteEndPoint — would be mis-sourced. Stay on the relay pair.
        if (_relay.IsActive)
            return;
        _transport.SetRemoteEndPoint(remoteEndPoint);
        _dtls.SetRemoteEndPoint(remoteEndPoint);
    }

    /// <summary>Test seam: whether the transport has switched onto the relay data path (RFC 8656 ChannelData).</summary>
    internal bool RelayDataPathActive => _relay.IsActive;

    // A relay pair won ICE: hand it to the relay data path, which switches the transport onto ChannelData
    // (RFC 8656 §11–12). Runs on the driver thread right after OnPairNominated has already pointed the
    // transport's remote and DTLS at the peer — the precondition EnterRelayMode needs.
    private void OnRelayPairNominated(IPEndPoint peer) => _relay.OnRelayPairNominated(peer);

    /// <summary>
    /// The bundle's sender-side transport-wide congestion controller (transport-cc), or
    /// <see langword="null"/> when the extension was not negotiated. Exposes the recommended outbound bitrate,
    /// its change event, and coarse network quality; the public WebRTC facade projects these onto its own
    /// reactive surface via <c>WebRtcCongestionRelay</c> (4.7.0 congestion API).
    /// </summary>
    internal TransportCcCongestionController? Congestion => _congestion?.Controller;

    /// <summary>Point-in-time transport counters aggregated from the outbound and inbound pipelines, the video
    /// track's frame/feedback counters, and the sender-side congestion controller's recommended bitrate.</summary>
    public BundledMediaStats SnapshotStats()
    {
        // Video counters are summed across all tracks (P2b), and surfaced as null on an audio-only bundle so the
        // "no video track" case stays distinguishable from a video track that has simply received nothing yet.
        var hasVideo = _video.Any;
        var video = _video.SnapshotStats();
        return new(
            _outbound.PacketsSent, _outbound.BytesSent, _outbound.SuppressedSends,
            _inbound.RtpPacketsReceived, _inbound.RtpBytesReceived, _inbound.DroppedDatagrams,
            hasVideo ? video.FramesReceived : null, hasVideo ? video.KeyFrames : null,
            hasVideo ? video.FramesDropped : null, hasVideo ? video.NacksSent : null, hasVideo ? video.PlisSent : null,
            Congestion?.RecommendedBitrateBps);
    }

    /// <summary>
    /// Point-in-time derived quality: the RTCP outbound metrics (RFC 3550 §6.4.1 — round-trip time and the loss
    /// the peer reports on our media, both <see langword="null"/> until the peer echoes a matching report) folded
    /// together with our own local receive-side interarrival jitter (RFC 3550 §A.8, <see langword="null"/> until
    /// an inbound clock rate is established).
    /// </summary>
    public BundledMediaQuality SnapshotQuality() =>
        _outboundQuality.Snapshot() with { JitterMs = _receptionStats.SnapshotJitterMs() };

    /// <summary>
    /// Point-in-time derived quality per media stream (CF-004f): the per-SSRC outbound RTT/loss (RFC 3550 §6.4.1)
    /// and the per-SSRC inbound interarrival jitter (RFC 3550 §A.8) folded together by MID. See
    /// <see cref="BundledMediaSessionComposition.FoldStreamQuality"/> for the full attribution rules; every metric
    /// is <see langword="null"/> until it is available.
    /// </summary>
    public IReadOnlyList<BundledStreamQuality> SnapshotStreamQuality() =>
        BundledMediaSessionComposition.FoldStreamQuality(
            _outboundQuality.SnapshotPerSsrc(), _receptionStats.SnapshotJitterMsPerSsrc(), _attribution.OutboundIdentity);

    /// <summary>
    /// Starts the shared receive loop, the ICE consent loop, and the DTLS handshake. Idempotent
    /// (#157 P2-7): a second call returns the first call's task instead of racing a second receive loop
    /// and DTLS handshake over the same datagram queue, so a later call's cancellation token is not
    /// honoured. The lock spans only owned code — the core yields inside the transport start (K3).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_startGate)
            return _startTask ??= StartCoreAsync(cancellationToken);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _ice.Start();
        // Keep a gathered relay allocation alive for the session (RFC 8656 §3.9). Idempotent — AdoptRelay may
        // already have started it for an answerer.
        _relay.Start();
        // Start emitting periodic Sender Reports (RFC 3550 §6.4). Its SRTCP send fails closed until the DTLS
        // handshake below installs the outbound SRTCP key, so an early start just suppresses the first ticks.
        _rtcpReporter.Start();
        // Start the transport-cc receive-side feedback loop (transport-cc), when negotiated. Its SRTCP send fails
        // closed the same way, so early ticks before keying are harmless (an empty batch or a suppressed send).
        _congestion?.Start();
        _dtls.Start(cancellationToken);
    }

    /// <summary>
    /// Sends one audio RTP payload on the audio track (suppressed until DTLS keys the transport). A no-op when
    /// the negotiation did not enable outbound audio (<see cref="AudioSendEnabled"/> is false) — the remote
    /// will not receive it, so nothing is streamed even if the caller keeps feeding audio.
    /// </summary>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, bool marker = false, CancellationToken cancellationToken = default)
        => _audioSendEnabled
            ? _outbound.SendAsync(_audioMid, payload, marker, cancellationToken: cancellationToken)
            : default;

    /// <summary>
    /// Internal seam: sends one audio RTP payload on the additional audio track <paramref name="mid"/> (4.7.0),
    /// stamping the explicit <paramref name="rtpTimestamp"/> on the outbound packets (RFC 3550 §5.1) rather than a
    /// cursor value, so an SFU forwarding this stream preserves the source's timestamp (A/V-sync against forwarded
    /// video). Suppressed until DTLS keys the transport; the track's timestamp cursor is not advanced. Backs the
    /// public <see cref="WebRtc.IAudioTrack"/> handle via <c>WebRtcPeerConnection.SendAudioTrackFrameAsync</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This bundle has no additional audio track with that MID.</exception>
    internal ValueTask SendAudioTrackFrameAsync(
        string mid, ReadOnlyMemory<byte> payload, uint rtpTimestamp, bool marker = false, CancellationToken cancellationToken = default)
        => _audioTracks.SendAsync(mid, payload, rtpTimestamp, marker, cancellationToken);

    /// <summary>
    /// Sends one out-of-band DTMF tone as an RFC 4733 telephone-event burst on the audio track: an event-start
    /// packet (marker set, half the duration) followed by two end-of-event packets (E-bit set, full duration —
    /// the second a reliability retransmission per RFC 4733 §2.5.1.4), all sharing one RTP timestamp on the
    /// telephone-event payload type, after which the audio track's timestamp cursor is advanced past the event so
    /// a following tone is distinctly timestamped. A no-op when outbound audio was not negotiated
    /// (<see cref="AudioSendEnabled"/> is false) — like <see cref="SendAudioAsync"/>, since the remote will not
    /// process the telephone-event stream. Fails closed like all bundle sends — suppressed until the DTLS
    /// handshake keys the transport (never leaves as plaintext).
    /// </summary>
    /// <param name="toneCode">The DTMF event code (0–9, 10=*, 11=#, 12–15=A–D per RFC 4733 §3.2).</param>
    /// <param name="durationMs">The tone duration in milliseconds (at least the RFC 4733 floor).</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toneCode"/> exceeds 15, or the duration is below the floor.</exception>
    /// <exception cref="InvalidOperationException">telephone-event was not negotiated for this session.</exception>
    public async Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default)
    {
        if (toneCode > 15)
            throw new ArgumentOutOfRangeException(nameof(toneCode), toneCode, "DTMF tone code must be between 0 and 15.");
        if (durationMs < RtpTelephoneEventCodec.MinDurationMs)
            throw new ArgumentOutOfRangeException(
                nameof(durationMs), durationMs, $"DTMF duration must be at least {RtpTelephoneEventCodec.MinDurationMs} ms.");

        // Outbound audio was not negotiated (a send-only/inactive remote answer, or a local side that does not
        // send): the remote will not process the telephone-event stream, so the burst is a no-op — mirroring
        // SendAudioAsync — instead of leaking DTMF onto a stream the peer declared it will not receive.
        if (!_audioSendEnabled)
            return;

        var payloadType = _telephoneEventPayloadType
            ?? throw new InvalidOperationException("RTP telephone-event (DTMF) was not negotiated for this WebRTC session.");

        // The RFC 4733 burst (start + two end-of-event packets sharing one timestamp on the telephone-event PT,
        // advancing the audio cursor past the event) is emitted by the composition helper — extracted so this
        // session stays under the size limit.
        await BundledMediaSessionComposition.SendDtmfBurstAsync(
            _outbound, _audioMid, toneCode, durationMs, _telephoneEventClockRate, (byte)payloadType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Packetises and sends one encoded video frame on the primary (non-simulcast) video track. Backward
    /// compatible with the pre-P2b path: with several video tracks this addresses the primary (first) track —
    /// use <see cref="SendVideoTrackFrameAsync(string, System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>
    /// to target a specific MID.
    /// </summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track, or the primary is simulcast.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default, bool? isKeyFrame = null)
        => _video.Primary is { } video
            ? video.SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken, isKeyFrame)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>Packetises and sends one encoded video frame on the primary track's simulcast <paramref name="rid"/> layer (RFC 8853).</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default, bool? isKeyFrame = null)
        => _video.Primary is { } video
            ? video.SendFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken, isKeyFrame)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>
    /// Packetises and sends one encoded video frame on the video track identified by <paramref name="mid"/>
    /// (P2b: N video tracks — e.g. a camera and a screen-share on distinct MIDs). Internal seam; the public
    /// add-a-track surface is P2c.
    /// </summary>
    /// <param name="mid">The MID of the target video track.</param>
    /// <exception cref="InvalidOperationException">This bundle has no video track with that MID.</exception>
    /// <exception cref="InvalidOperationException">The target track is simulcast (send with a rid instead).</exception>
    public Task SendVideoTrackFrameAsync(string mid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => (_video.Find(mid) ?? throw NoVideoTrack(mid)).SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);

    /// <summary>
    /// Packetises and sends one encoded video frame on the <paramref name="mid"/> video track's simulcast
    /// <paramref name="rid"/> layer (RFC 8853). Internal seam; the public add-a-track surface is P2c.
    /// </summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track with that MID.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendVideoTrackFrameAsync(string mid, string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => (_video.Find(mid) ?? throw NoVideoTrack(mid)).SendFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken);

    private static InvalidOperationException NoVideoTrack(string mid)
        => new($"This bundle has no video track with MID '{mid}'.");

    /// <summary>
    /// Asks the peer for a fresh video key frame on the primary track, on the app's demand (RFC 4585 §6.3.1).
    /// A no-op returning <see langword="false"/> when this bundle has no video track, when the peer did not
    /// advertise PLI, or when the 500 ms throttle still holds; otherwise sends the PLI and returns
    /// <see langword="true"/>. Addresses the primary (first) track — use <see cref="RequestVideoTrackKeyFrameAsync"/>
    /// to target a specific MID.
    /// </summary>
    public ValueTask<bool> RequestVideoKeyFrameAsync(CancellationToken cancellationToken = default)
        => _video.Primary is { } video
            ? video.RequestKeyFrameAsync(cancellationToken)
            : ValueTask.FromResult(false);

    /// <summary>
    /// Asks the peer for a fresh video key frame on the <paramref name="mid"/> video track (P2b). A no-op
    /// returning <see langword="false"/> when this bundle has no such track, when the peer did not advertise
    /// PLI, or when the throttle still holds. Internal seam; the public add-a-track surface is P2c.
    /// </summary>
    public ValueTask<bool> RequestVideoTrackKeyFrameAsync(string mid, CancellationToken cancellationToken = default)
        => _video.Find(mid) is { } video
            ? video.RequestKeyFrameAsync(cancellationToken)
            : ValueTask.FromResult(false);

    /// <summary>
    /// Tears the session down: stops ICE and DTLS (closing the association, zeroing keys) before
    /// disposing the video tracks and finally the transport (which stops the receive loop and the socket).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Mark disposed under the mutation gate so a concurrent AddVideoTrack either completes before teardown
        // (and its track is disposed by _video.Dispose() below) or fails fast — never wires a track onto a
        // half-torn-down session. Take the gate only to flip the flag; the async teardown runs outside it.
        lock (_trackMutationGate)
            Volatile.Write(ref _disposed, 1);

        await _ice.DisposeAsync().ConfigureAwait(false);
        // Tear the relay path down after ICE (the driver is stopped, so no new nomination starts a transition)
        // and before the transport: it drains an in-flight transition, then stops the channel rebind and the
        // allocation keepalive — both of which ride the transport's control send.
        await _relay.DisposeAsync().ConfigureAwait(false);
        // Stop the transport-cc congestion plane before the transport it rides is torn down (its SRTCP feedback
        // send goes through the transport): its dispose signals the lifetime token and awaits the loop.
        if (_congestion is not null)
            await _congestion.DisposeAsync().ConfigureAwait(false);
        // Stop the periodic Sender Reports before the transport it rides is torn down (its SRTCP send goes
        // through the transport), and before DTLS zeroes the outbound SRTCP key.
        await _rtcpReporter.DisposeAsync().ConfigureAwait(false);
        await _dtls.DisposeAsync().ConfigureAwait(false);
        _video.Dispose();
        // Dispose the tracks SetVideoTrackInactive deferred — safe now (send gate drained, receive loop stopping;
        // _disposed set, so no concurrent SetVideoTrackInactive can add here).
        foreach (var deactivated in _deactivatedVideoTracks)
            deactivated.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
