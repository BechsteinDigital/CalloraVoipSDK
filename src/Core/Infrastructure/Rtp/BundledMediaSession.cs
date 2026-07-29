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
    // The bundle's video tracks (P2b: N video m-lines, RFC 8843 §9), keyed by MID. Empty for an audio-only
    // bundle; the first is the primary, addressed by the mid-less send/receive facade for backward compatibility.
    private readonly BundledVideoTrackSet _video;
    // The bundle's ADDITIONAL inbound audio tracks (4.7.0: N audio m-lines, RFC 8843 §9), keyed by MID. Empty for
    // a single-audio bundle. The PRIMARY audio (options.Audio, the transport anchor) is NOT in this set — it keeps
    // the mid-less AudioReceived event; these extra receive-only sinks surface on the mid-tagged event instead.
    private readonly BundledAudioTrackSet _audioTracks;

    // Transport-wide congestion control (transport-cc / RFC 8888), one plane for the WHOLE bundle because
    // transport-cc numbers the transport, not a stream. Null unless the a=extmap was negotiated. See
    // BundledCongestionPlane — the sender-side controller (recommended bitrate) and the receive-side feedback
    // sender, with their own lifetime token; OnControlPacketReceived fans decoded feedback into it.
    private readonly BundledCongestionPlane? _congestion;

    private readonly string _audioMid;
    private readonly uint _audioSsrc;
    private readonly bool _audioSendEnabled;
    // Our local sending SSRCs mapped to the track they belong to (MID + kind), so a per-SSRC outbound quality
    // snapshot (RTT/loss keyed per our sending SSRC) can be attributed to a stream. Audio SSRC → audio MID;
    // each video/simulcast-encoding SSRC → video MID. Read-only after construction.
    private readonly IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> _outboundStreamIdentity;
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
    private int _disposed;

    // Tracks removed by SetVideoTrackInactive: routing already dropped, but disposed only in DisposeAsync (never
    // live — BundledVideoTrack.Dispose needs in-flight send/receive drained first, HARD-C6). Added under the gate.
    private readonly List<BundledVideoTrack> _deactivatedVideoTracks = [];

    // Every outbound SSRC live on the bundle right now (RFC 3550 §8.1): seeded from the ctor tracks, extended on
    // AddVideoTrack and pruned on SetVideoTrackInactive under _trackMutationGate, so OutboundSsrcs always reflects
    // the SSRCs in use — the seed a renegotiator allocates a new track's SSRCs against. MID-keyed internally so a
    // deactivated track's SSRCs are released exactly (BundledVideoTrack does not expose its SSRCs).
    private readonly BundledOutboundSsrcTracker _outboundSsrcs;

    // RFC 4733 inbound DTMF reassembly (extracted to BundledInboundDtmfReassembler). Driven only by
    // RaiseAudioReceived, which runs solely on the single shared receive loop, so the reassembler needs no
    // synchronization. Null when the peer did not negotiate telephone-event (no reassembly path).
    private readonly BundledInboundDtmfReassembler? _dtmfReassembler;
    // 0 = no relay candidate wired; 1 = wired (at construction from the options factory, or later via
    // AdoptRelay). Guards against wiring the relay path twice (a second indication relay / relay candidate).
    private int _relayWired;
    // The relay allocation keepalive (RFC 8656 §3.9), when a relay path was wired: started with the session and
    // disposed — running its teardown Refresh(0) — before the transport it rides. Set from the relay binding at
    // construction (offerer) or via AdoptRelay (answerer); Volatile for the gather→start/dispose cross-thread read.
    private IRelayKeepAlive? _relayKeepAlive;
    // The relay binding (its ChannelBind seam + relay server), retained so a relay-pair nomination can switch the
    // transport onto the relay data path. Set from the binding at construction (offerer) or AdoptRelay (answerer).
    private RelayIceBinding? _relayBinding;
    // The one-shot direct→relay data-path transition, kicked off on the driver thread when a relay pair is
    // nominated. Guarded so it runs at most once; cancelled and awaited before the transport is disposed (its
    // ChannelBind + EnterRelayMode ride the live transport).
    private int _relayTransitionStarted;
    private Task? _relayTransitionTask;
    private readonly CancellationTokenSource _relayTransitionCts = new();
    // Set once the transition actually SUCCEEDED (channel installed) — not merely started, so a failed ChannelBind
    // (transition abandoned, media back on the checked path) still lets a later nomination re-point the transport.
    // Once set, the transport is relay-committed to the bound peer: a later relay→direct re-nomination must not
    // re-point its remote (the bound channel forwards to the relay peer; re-pointing would mis-attribute inbound).
    private int _relayTransitioned;
    // The channel rebind keepalive (RFC 8656 §12), set once the relay data-path transition binds a channel:
    // started right after SetRelayChannel and disposed — before the transport it rides — in DisposeAsync. The
    // channel exists only after the transition, so this starts later than the allocation/permission keepalive.
    // Volatile for the transition-thread write / dispose-thread read.
    private IRelayKeepAlive? _channelRebind;

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
    /// Raised with each reassembled inbound video frame on the <em>primary</em> video track (frame, RTP
    /// timestamp, is-key-frame). Backward-compatible with the pre-P2b single-video path: with exactly one
    /// video track this fires for that track's frames; with several it fires only for the primary (first)
    /// track. Use <see cref="VideoTrackFrameReceived"/> to receive every track's frames tagged with its MID.
    /// </summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    /// <summary>
    /// Raised with each reassembled inbound video frame on any video track (P2b), tagged with the MID of the
    /// track it arrived on (MID, frame, RTP timestamp, is-key-frame). Fires for every video track — the way to
    /// tell N video tracks apart on the inbound path. Runs on the shared receive loop.
    /// </summary>
    public event Action<string, byte[], uint, bool>? VideoTrackFrameReceived;

    /// <summary>
    /// Raised when the peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104) on the video track;
    /// the app should encode and send a key frame.
    /// </summary>
    public event Action? VideoKeyFrameRequested;

    /// <summary>Raised when the shared DTLS handshake fails — media stays blocked (fail closed).</summary>
    public event Action? HandshakeFailed;

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
        // Inbound DTMF reassembler only when telephone-event was negotiated (RFC 4733): it fires DtmfReceived on a
        // completed tone. Driven solely by the receive loop (via RaiseAudioReceived), so it needs no locking.
        _dtmfReassembler = _telephoneEventPayloadType is not null
            ? new BundledInboundDtmfReassembler(_telephoneEventClockRate, (tone, ms) => DtmfReceived?.Invoke(tone, ms), _logger)
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
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, payloadTypesByMid));
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
            WireVideoTrackEvents(mid, track, isPrimary);
            router.RegisterTrack(mid, track.OnRtpPacket);
            builtVideo.Add((mid, track));
        }

        _video = builtVideo.Count > 0 ? new BundledVideoTrackSet(builtVideo) : new BundledVideoTrackSet();

        // Transport-wide congestion control (transport-cc / RFC 8888), one plane per bundle. Only when the
        // a=extmap was negotiated (so the transport actually stamps a transport-wide sequence) — otherwise the
        // plane stays off. See BundledCongestionPlane: it wires the sender-side controller to PacketSent and the
        // receive-side feedback sender to inbound RTP; OnControlPacketReceived fans decoded feedback into it.
        if (options.TransportWideCcExtensionId is { } transportCcExtensionId)
            _congestion = new BundledCongestionPlane(
                transportCcExtensionId, _outbound, _inbound, _rtcpCodec, options.Audio.Ssrc, loggerFactory);

        // One shared DTLS association keys every track; one shared ICE agent keeps the group alive.
        _dtls = new BundledDtlsKeying(
            options.DtlsIsClient, options.RemoteEndPoint, options.RemoteFingerprint,
            handshaker, certificate, _inbound, _outbound, _transport,
            onHandshakeFailed: () => HandshakeFailed?.Invoke(), loggerFactory,
            onKeysInstalled: () => Connected?.Invoke());

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
        // Seed the live outbound-SSRC bookkeeping (RFC 3550 §8.1) from the ctor tracks so OutboundSsrcs reflects the
        // SSRCs in use from the start — the seed a renegotiation allocates around.
        _outboundSsrcs = new BundledOutboundSsrcTracker(options.Audio.Ssrc);
        foreach (var video in options.VideoTracks)
            _outboundSsrcs.Add(video.Mid, video);
        _outboundStreamIdentity = BundledMediaSessionComposition.BuildOutboundStreamIdentity(options);
        // A relay candidate wired at construction (offerer path) closes the door on a later AdoptRelay.
        _relayWired = relayBinding is not null ? 1 : 0;
        // Its keepalive (if any) is started in StartAsync, once the transport's receive loop is up.
        _relayKeepAlive = relayBinding?.KeepAlive;
        // Retained so a relay-pair nomination can switch the transport onto the relay data path.
        _relayBinding = relayBinding;
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

    // Decodes an inbound decrypted RTCP compound (RFC 3550 §6.4.1). Two directions: every Sender Report's LSR
    // (middle 32 NTP bits) + arrival is recorded per sender SSRC so our next report echoes LSR/DLSR back for the
    // peer's RTT; and every report block the peer sends about OUR outbound streams (carried in an inbound SR or
    // RR) feeds the outbound quality tracker to derive our own RTT and the loss the peer sees. Runs on the
    // receive loop; a malformed compound must not tear it down, so decode failures are swallowed with a log.
    private void OnControlPacketReceived(byte[] rtcp)
    {
        // Monotonic arrival for the RTT delta (matched against the SR's monotonic send instant) so a system-
        // clock step between sending our SR and its echo arriving cannot corrupt the derived RTT.
        var arrival = MonotonicClock.Now;

        IReadOnlyList<RtcpPacket> packets;
        try
        {
            packets = _rtcpCodec.Decode(rtcp);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Ignoring undecodable inbound RTCP compound on the bundle path.");
            return;
        }

        foreach (var packet in packets)
        {
            switch (packet)
            {
                case RtcpSenderReport senderReport:
                    _receptionStats.RecordSenderReport(senderReport.Ssrc, senderReport.NtpTimestamp);
                    RecordRemoteReportBlocks(senderReport.ReportBlocks, arrival);
                    break;
                case RtcpReceiverReport receiverReport:
                    RecordRemoteReportBlocks(receiverReport.ReportBlocks, arrival);
                    break;
            }
        }

        // Fan the already-decoded compound out to every video track for RTCP feedback (PLI/FIR → keyframe
        // request; Generic NACK → RTX). Each track filters to its own SSRC, so a NACK for one track never
        // resends another's. Runs on this same receive-loop thread, so each track's confinement is preserved.
        _video.OnRtcpPackets(packets);

        // And to the transport-wide congestion controller: any transport-cc feedback report in the compound
        // (RFC 8888) updates its delay-trend + loss estimators and the recommended bitrate. Same thread — no
        // added confinement concern.
        _congestion?.OnRtcpPackets(packets);
    }

    // Feeds the peer's reception report blocks (about our outbound streams) into the outbound quality tracker.
    private void RecordRemoteReportBlocks(IReadOnlyList<RtcpReportBlock> blocks, DateTimeOffset arrival)
    {
        foreach (var block in blocks)
            _outboundQuality.RecordRemoteReportBlock(
                block.Ssrc, block.FractionLost, block.LastSr, block.DelaySinceLastSr, arrival);
    }

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>
    /// The remote peer's ICE username fragment the shared transport was built with (RFC 8839), or null when the
    /// exchange carried no remote ICE credentials. A renegotiator compares it against a re-offer's ufrag to detect
    /// an ICE restart (RFC 8829 §5.3.1), which the live track-diff path does not support.
    /// </summary>
    public string? RemoteIceUfrag => _options.Ice.RemoteIceUfrag;

    /// <summary>The local audio track's synchronisation source.</summary>
    public uint AudioSsrc => _audioSsrc;

    /// <summary>
    /// A snapshot of every outbound synchronisation source live on the bundle right now (RFC 3550 §8.1): the
    /// audio SSRC plus each active video track's primary/per-encoding and RTX SSRC(s). A renegotiator seeds its
    /// SSRC allocation with this so a mid-call-added track's SSRCs stay distinct from every SSRC already in use —
    /// a shared SSRC would collide the per-SSRC SRTP context (ROC/replay keyed by SSRC). The set is snapshotted
    /// under the track-mutation gate, so it reflects a consistent point between live add/remove mutations.
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
        ArgumentNullException.ThrowIfNull(relayIceBindingFactory);
        if (Interlocked.Exchange(ref _relayWired, 1) != 0)
            return;

        var binding = relayIceBindingFactory.Invoke(_transport.SendUnframedAsync);
        if (binding is null)
        {
            // No allocation after all — release the claim so a later adoption can still wire the relay path.
            Volatile.Write(ref _relayWired, 0);
            return;
        }

        _transport.SetIndicationRelay(binding.Indication, binding.OnControl);
        // Hand the ICE agent both the relay send path and the per-peer permission installer: a controlled
        // (answerer) agent uses the installer to proactively permission the offerer's remote-candidate IPs
        // (RFC 8656 §9) so their inbound relay checks reach it rather than being dropped by the TURN server.
        _ice.AddRelayLocalCandidate(binding.RelaySend, binding.EnsurePermission);
        // Retain the binding so a later relay-pair nomination can ChannelBind + switch the transport.
        Volatile.Write(ref _relayBinding, binding);

        // Keep the adopted allocation alive. Started here (idempotent) so an adoption that lands after StartAsync
        // still runs the keepalive; the StartAsync start covers the pre-start case. Starting before the transport
        // receive loop is up is safe — the first refresh is roughly half the allocation lifetime away.
        Volatile.Write(ref _relayKeepAlive, binding.KeepAlive);
        binding.KeepAlive?.Start();
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
    public void AddVideoTrack(BundledTrackConfig video)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentException.ThrowIfNullOrEmpty(video.Mid, nameof(video));

        lock (_trackMutationGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new InvalidOperationException("Cannot add a video track to a disposed bundled media session.");
            if (_video.Find(video.Mid) is not null)
                throw new InvalidOperationException($"A video track with MID '{video.Mid}' already exists on this bundle.");

            // 1. Extend the demux boundary FIRST: inbound packets for the new MID are now accepted (rather than
            //    rejected as an unknown MID) and, until the sink is registered below, cleanly dropped/counted.
            _router.AddKnownMid(video.Mid);

            // 2. Register the outbound sender(s) for the MID (simulcast: one per a=rid encoding; plain: one, with
            //    RTX when negotiated) — identical to the ctor path — and build the track that will be its sink.
            //    BuildVideoTrack registers the outbound sender(s) as a side effect and returns the inbound track.
            var track = BundledMediaSessionComposition.BuildVideoTrack(_options, video, _outbound, _loggerFactory);

            // 3. Wire the track's inbound frame / key-frame events. A live-added track is never the primary, so it
            //    fires only the mid-tagged VideoTrackFrameReceived, leaving the mid-less facade on the ctor primary.
            WireVideoTrackEvents(video.Mid, track, isPrimary: false);

            // 4. Register the inbound router sink LAST, so no packet can hit a half-built track: only now can an
            //    inbound datagram for the new MID reach a live, fully-wired track.
            _router.RegisterTrack(video.Mid, track.OnRtpPacket);

            // 5. Publish to the video set so the send API and RTCP feedback fan-out find it.
            if (!_video.TryAdd(video.Mid, track))
            {
                // Lost a race we hold the gate against — should be unreachable. Unwind the partial wiring so no
                // orphaned sink/sender lingers, and surface it rather than leak a half-registered track.
                _router.UnregisterTrack(video.Mid);
                _outbound.UnregisterTrack(video.Mid);
                track.Dispose();
                throw new InvalidOperationException($"A video track with MID '{video.Mid}' already exists on this bundle.");
            }

            // 6. Record the track's SSRCs as live (RFC 3550 §8.1) so a later renegotiation allocates around them.
            _outboundSsrcs.Add(video.Mid, video);
        }
    }

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
    public void SetVideoTrackInactive(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);

        lock (_trackMutationGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return; // teardown in progress — every track is disposed by DisposeAsync.

            // Inbound first: no further datagram for this MID reaches a sink (the router drops/counts it instead).
            _router.UnregisterTrack(mid);
            // Then outbound: every RID layer registered under the MID is removed, so no further frame is sent.
            _outbound.UnregisterTrack(mid);
            // Drop it from the set, but do NOT dispose here: the receive loop may be inside its OnRtpPacket (a
            // loss-triggered feedback send reads its lifetime token), and a live dispose would throw
            // ObjectDisposedException on the loop → whole-bundle teardown. Defer to DisposeAsync (HARD-C6 drain).
            if (_video.Remove(mid) is { } removed)
                _deactivatedVideoTracks.Add(removed);
            // Release the track's SSRCs from the live bookkeeping so a later renegotiation may reuse them (the
            // per-SSRC SRTP context is gone with the track). No-op when the MID was already inactive (idempotent).
            _outboundSsrcs.Remove(mid);
        }
    }

    // Wires one video track's inbound events to the session's surface. The mid-less legacy VideoFrameReceived
    // tracks only the primary (ctor-first) track; the mid-tagged VideoTrackFrameReceived fires for every track
    // so N tracks are distinguishable on the inbound path. Used by both the ctor loop and the live AddVideoTrack.
    private void WireVideoTrackEvents(string mid, BundledVideoTrack track, bool isPrimary)
    {
        track.FrameReceived += (frame, timestamp, isKeyFrame) =>
        {
            if (isPrimary)
                VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame);
            VideoTrackFrameReceived?.Invoke(mid, frame, timestamp, isKeyFrame);
        };
        track.KeyFrameRequested += () => VideoKeyFrameRequested?.Invoke();
    }

    // A connectivity-checked ICE nomination (RFC 8445 §8) redirects the whole 5-tuple onto the nominated
    // pair: the transport's send target and the DTLS association's inbound source filter both follow it, so
    // the handshake completes against the checked candidate rather than the initial SDP endpoint.
    private void OnPairNominated(IPEndPoint remoteEndPoint)
    {
        // Once the relay data path is committed the transport is bound to the relay peer; a later re-nomination
        // (e.g. a direct path that only recovered after relay won) must not re-point the transport, or inbound
        // ChannelData — unwrapped and attributed to _remoteEndPoint — would be mis-sourced. Stay on the relay pair.
        if (Volatile.Read(ref _relayTransitioned) != 0)
            return;
        _transport.SetRemoteEndPoint(remoteEndPoint);
        _dtls.SetRemoteEndPoint(remoteEndPoint);
    }

    /// <summary>Test seam: whether the transport has switched onto the relay data path (RFC 8656 ChannelData).</summary>
    internal bool RelayDataPathActive => Volatile.Read(ref _relayTransitioned) != 0;

    // A relay pair won ICE: switch the transport onto the relay data path (RFC 8656). Runs on the driver thread
    // right after OnPairNominated has already pointed the transport's remote and DTLS at the peer (the
    // precondition EnterRelayMode needs), so it only kicks off the async transition — at most once — and returns.
    private void OnRelayPairNominated(IPEndPoint peer)
    {
        if (Interlocked.Exchange(ref _relayTransitionStarted, 1) != 0)
            return;
        Volatile.Write(ref _relayTransitionTask, Task.Run(() => TransitionToRelayAsync(peer)));
    }

    // ChannelBind the peer while the transport is still in direct mode (the request reaches the server unframed
    // via the relay control stack), then flip the transport into relay mode and install the bound channel — media
    // then flows as ChannelData through the TURN server (RFC 8656 §11–12). A failed ChannelBind leaves media on
    // the checked path (logged); a disposing session cancels it.
    private async Task TransitionToRelayAsync(IPEndPoint peer)
    {
        var binding = Volatile.Read(ref _relayBinding);
        if (binding?.BindChannel is not { } bindChannel)
            return;

        try
        {
            var channelBinding = await bindChannel(peer, _relayTransitionCts.Token).ConfigureAwait(false);
            // Re-assert the relay peer as the transport remote right before the flip, in case a direct
            // re-nomination re-pointed it during the (sub-second) ChannelBind — the bound channel forwards to
            // this peer, and inbound ChannelData is attributed to it.
            _transport.SetRemoteEndPoint(peer);
            _transport.EnterRelayMode(binding.Indication.RelayServer, binding.OnControl);
            _transport.SetRelayChannel(channelBinding.Channel);
            // Commit: from here a later re-nomination must not re-point the transport (see OnPairNominated).
            Volatile.Write(ref _relayTransitioned, 1);
            // Keep the channel binding alive (RFC 8656 §12): start the rebind loop now — the channel exists only
            // after this transition — and dispose it before the transport it rides (DisposeAsync).
            if (channelBinding.Rebind is { } channelRebind)
            {
                Volatile.Write(ref _channelRebind, channelRebind);
                channelRebind.Start();
            }
            _logger.LogInformation(
                "Relay data path activated for the nominated relay pair: media now flows as ChannelData through the " +
                "TURN server (RFC 8656 §11–12).");
        }
        catch (OperationCanceledException) when (_relayTransitionCts.IsCancellationRequested)
        {
            // Session disposing — abort the transition.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to switch onto the relay data path after nominating a relay pair; media stays on the checked path.");
        }
    }

    /// <summary>
    /// The bundle's sender-side transport-wide congestion controller (transport-cc / RFC 8888), or
    /// <see langword="null"/> when the extension was not negotiated. Exposes the recommended outbound bitrate
    /// and coarse network quality. Internal for now — surfacing it on the public WebRTC peer facade is a
    /// documented follow-up (mirrors the single-stream <c>VideoRtpStream.Congestion</c> internal accessor).
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
            _outboundQuality.SnapshotPerSsrc(), _receptionStats.SnapshotJitterMsPerSsrc(), _outboundStreamIdentity);

    /// <summary>Starts the shared receive loop, the ICE consent loop, and the DTLS handshake.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _ice.Start();
        // Keep a gathered relay allocation alive for the session (RFC 8656 §3.9). Idempotent — AdoptRelay may
        // already have started it for an answerer.
        Volatile.Read(ref _relayKeepAlive)?.Start();
        // Start emitting periodic Sender Reports (RFC 3550 §6.4). Its SRTCP send fails closed until the DTLS
        // handshake below installs the outbound SRTCP key, so an early start just suppresses the first ticks.
        _rtcpReporter.Start();
        // Start the transport-cc receive-side feedback loop (RFC 8888), when negotiated. Its SRTCP send fails
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
    /// suppressed until DTLS keys the transport. Drives the symmetric outbound sender wired at construction; there
    /// is no public N-audio send API in this slice, so this serves composition and the loopback tests.
    /// </summary>
    /// <exception cref="InvalidOperationException">This bundle has no additional audio track with that MID.</exception>
    internal ValueTask SendAudioTrackFrameAsync(
        string mid, ReadOnlyMemory<byte> payload, bool marker = false, CancellationToken cancellationToken = default)
        => _audioTracks.SendAsync(mid, payload, marker, cancellationToken);

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
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video.Primary is { } video
            ? video.SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>Packetises and sends one encoded video frame on the primary track's simulcast <paramref name="rid"/> layer (RFC 8853).</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video.Primary is { } video
            ? video.SendFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken)
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
        // Drain a relay data-path transition in flight before disposing the transport it rides: the driver is
        // now stopped (no new transition starts), so cancel and await the running one.
        await _relayTransitionCts.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _relayTransitionTask) is { } transition)
            await transition.ConfigureAwait(false);
        _relayTransitionCts.Dispose();
        // Dispose the channel rebind loop (RFC 8656 §12) before the allocation keepalive: both ride the
        // transport's control send (so both must run before the transport is disposed), and the rebind stops
        // first so it does not re-bind a channel the allocation teardown is about to drop.
        if (Volatile.Read(ref _channelRebind) is { } channelRebind)
            await channelRebind.DisposeAsync().ConfigureAwait(false);
        // Dispose the relay keepalive after ICE (no more relay checks) but before the transport: its teardown
        // Refresh(0) rides the transport's control send, so the transport must still be alive to carry it.
        if (Volatile.Read(ref _relayKeepAlive) is { } keepAlive)
            await keepAlive.DisposeAsync().ConfigureAwait(false);
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
