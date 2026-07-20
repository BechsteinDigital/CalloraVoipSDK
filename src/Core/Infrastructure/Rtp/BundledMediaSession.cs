using System.Net;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
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
/// an audio track and an optional video track (<see cref="BundledVideoTrack"/>, B4) over the inbound and
/// outbound pipelines (B2c-in). This is the object that ties the transport slices into one startable unit
/// — the internal composition a signalling-neutral WebRTC facade drives, or that the SDP negotiator
/// builds from a BUNDLE-negotiated offer/answer.
/// </summary>
internal sealed class BundledMediaSession : IAsyncDisposable
{
    private readonly BundledMediaTransport _transport;
    private readonly BundledOutboundPipeline _outbound;
    private readonly BundledInboundPipeline _inbound;
    private readonly BundledDtlsKeying _dtls;
    private readonly BundledIceControl _ice;
    private readonly BundledRtcpReporter _rtcpReporter;
    private readonly BundledInboundReceptionStats _receptionStats;
    private readonly BundledOutboundQualityTracker _outboundQuality;
    private readonly IRtcpPacketCodec _rtcpCodec;
    private readonly BundledVideoTrack? _video;
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

    // RFC 4733 inbound DTMF reassembly state. Touched only by RaiseAudioReceived, which runs solely on the
    // single shared receive loop (the inbound pipeline dispatches sequentially per the transport's one receive
    // task) — no other thread reads or writes it, so no synchronization is needed. Keep it that way: any new
    // reader from another thread must add explicit synchronization.
    private bool _hasPendingDtmfEvent;
    private uint _pendingDtmfSsrc;
    private uint _pendingDtmfTimestamp;
    private byte _pendingDtmfToneCode;
    private ushort _pendingDtmfDurationRtpUnits;
    private bool _pendingDtmfCompleted;
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

    /// <summary>Raised with each decrypted inbound audio RTP packet.</summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>
    /// Raised once per fully received inbound RFC 4733 telephone-event (DTMF), carrying the tone code (0–15)
    /// and the reassembled tone duration in milliseconds. Fired on the shared receive loop from the event's
    /// end-of-event packet; telephone-event packets are consumed here and never surfaced on
    /// <see cref="AudioReceived"/>.
    /// </summary>
    public event Action<byte, int>? DtmfReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

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

        _audioMid = options.Audio.Mid;
        _audioSendEnabled = options.AudioSendEnabled;
        _telephoneEventPayloadType =
            options.Audio.TelephoneEventPayloadType is >= 0 and <= 127 ? options.Audio.TelephoneEventPayloadType : null;
        _telephoneEventClockRate = options.Audio.TelephoneEventClockRate > 0 ? options.Audio.TelephoneEventClockRate : 8000;
        _logger = loggerFactory.CreateLogger<BundledMediaSession>();

        // Inbound: demux the shared socket by the negotiated m-lines' payload types, route each MID.
        var payloadTypesByMid = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
        {
            [options.Audio.Mid] = new[] { (int)options.Audio.PayloadType },
        };
        if (options.Video is { } videoConfig)
            payloadTypesByMid[videoConfig.Mid] = new[] { (int)videoConfig.PayloadType };

        var router = new BundledTrackRouter(
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, payloadTypesByMid));
        router.RegisterTrack(options.Audio.Mid, RaiseAudioReceived);

        // Per-SSRC inbound reception statistics (RFC 3550 §6.4.1) feed the periodic RTCP report blocks: the
        // inbound pipeline records each decoded RTP packet, and inbound SRs feed LSR/DLSR (subscribed below).
        // The negotiated clock/kind is applied per inbound source by matching the first packet's payload type
        // (the inbound SSRC is the remote's choice), so audio gets its exact §A.8 clock and video gets 90 kHz
        // regardless of arrival order, and each source is attributed to its track (CF-004f).
        _receptionStats = new BundledInboundReceptionStats(clockByPayloadType: BuildInboundClockMap(options));
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

        // Outbound: a per-track sender for each m-line, stamping its MID.
        _outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), _transport, loggerFactory.CreateLogger<BundledOutboundPipeline>());
        _outbound.RegisterTrack(options.Audio.Mid, BuildOutboundTrack(options, options.Audio));

        if (options.Video is { } video)
        {
            var codecName = video.VideoCodecName
                ?? throw new ArgumentException("A video track must name its codec.", nameof(options));

            if (video.Encodings.Count > 0)
            {
                // Send-side simulcast (RFC 8853): one outbound RTP stream per a=rid layer under the shared
                // MID, each on its own SSRC with the negotiated RID header extension (RFC 8852) stamped.
                var ridExtensionId = options.RidExtensionId ?? throw new ArgumentException(
                    "A simulcast video track needs a negotiated RID header-extension id.", nameof(options));
                foreach (var encoding in video.Encodings)
                    _outbound.RegisterTrack(video.Mid, encoding.Rid,
                        BuildEncodingTrack(options, video.Mid, encoding.Ssrc, video.PayloadType, encoding.Rid, ridExtensionId));

                _video = new BundledVideoTrack(
                    video.Mid, codecName, video.PayloadType,
                    video.Encodings.Select(e => e.Rid).ToArray(),
                    _outbound, options.VideoReorderDepth, loggerFactory.CreateLogger<BundledVideoTrack>());
            }
            else
            {
                _outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
                _video = new BundledVideoTrack(
                    video.Mid, codecName, video.PayloadType, _outbound, options.VideoReorderDepth,
                    loggerFactory.CreateLogger<BundledVideoTrack>());
            }

            _video.FrameReceived += (frame, timestamp, isKeyFrame) => VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame);
            router.RegisterTrack(video.Mid, _video.OnRtpPacket);
        }

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
        _outboundStreamIdentity = BuildOutboundStreamIdentity(options);
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
        if (_telephoneEventPayloadType is { } telephoneEventPayloadType
            && packet.PayloadType == telephoneEventPayloadType)
        {
            HandleInboundTelephoneEvent(packet);
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

    // Reassembles an inbound RFC 4733 telephone-event stream (RFC 4733 §2.5.1.2): a DTMF tone is carried by a
    // burst of packets sharing one RTP timestamp with a growing duration, the last marked end-of-event (E-bit).
    // The complete tone is surfaced once, on the first end-of-event packet, with the reassembled duration.
    // Mirrors the SIP path (RtpCallMediaSession.HandleInboundTelephoneEvent) so both paths behave alike. Runs
    // solely on the shared receive loop, so the reassembly state needs no synchronization.
    private void HandleInboundTelephoneEvent(RtpPacket packet)
    {
        if (!RtpTelephoneEventCodec.TryParse(
                packet.Payload.Span, out var toneCode, out var endOfEvent, out var durationRtpUnits))
        {
            _logger.LogDebug(
                "Ignoring malformed telephone-event RTP payload from SSRC={Ssrc:X8} (payloadLength={PayloadLength}).",
                packet.Ssrc, packet.Payload.Length);
            return;
        }

        if (toneCode > 15)
        {
            _logger.LogDebug("Ignoring unsupported telephone-event code {ToneCode}; supported range is 0-15.", toneCode);
            return;
        }

        var isSameEvent =
            _hasPendingDtmfEvent &&
            _pendingDtmfSsrc == packet.Ssrc &&
            _pendingDtmfTimestamp == packet.Timestamp &&
            _pendingDtmfToneCode == toneCode;

        if (!isSameEvent)
        {
            _hasPendingDtmfEvent = true;
            _pendingDtmfSsrc = packet.Ssrc;
            _pendingDtmfTimestamp = packet.Timestamp;
            _pendingDtmfToneCode = toneCode;
            _pendingDtmfDurationRtpUnits = durationRtpUnits;
            _pendingDtmfCompleted = false;
        }
        else if (durationRtpUnits > _pendingDtmfDurationRtpUnits)
        {
            _pendingDtmfDurationRtpUnits = durationRtpUnits;
        }

        if (!endOfEvent || _pendingDtmfCompleted)
            return;

        _pendingDtmfCompleted = true;
        var durationMs = RtpTelephoneEventCodec.DurationRtpUnitsToMs(_pendingDtmfDurationRtpUnits, _telephoneEventClockRate);

        try
        {
            DtmfReceived?.Invoke(toneCode, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled DtmfReceived handler.");
        }
    }

    // Decodes an inbound decrypted RTCP compound (RFC 3550 §6.4.1). Two directions: every Sender Report's LSR
    // (middle 32 NTP bits) + arrival is recorded per sender SSRC so our next report echoes LSR/DLSR back for the
    // peer's RTT; and every report block the peer sends about OUR outbound streams (carried in an inbound SR or
    // RR) feeds the outbound quality tracker to derive our own RTT and the loss the peer sees. Runs on the
    // receive loop; a malformed compound must not tear it down, so decode failures are swallowed with a log.
    private void OnControlPacketReceived(byte[] rtcp)
    {
        var arrival = DateTimeOffset.UtcNow;

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
    }

    // Feeds the peer's reception report blocks (about our outbound streams) into the outbound quality tracker.
    private void RecordRemoteReportBlocks(IReadOnlyList<RtcpReportBlock> blocks, DateTimeOffset arrival)
    {
        foreach (var block in blocks)
            _outboundQuality.RecordRemoteReportBlock(
                block.Ssrc, block.FractionLost, block.LastSr, block.DelaySinceLastSr, arrival);
    }

    // The RTP clock rate used for the SR RTP-timestamp extrapolation (CF-004e). Audio uses its negotiated codec
    // clock (from the track config); video uses the fixed 90 kHz RTP clock (RFC 3551 §5) — the bundle video
    // track config does not carry a per-codec rate, and all supported video codecs (H.264/VP8) run at 90 kHz.
    private const uint VideoRtpClockRate = 90000;

    // Maps each negotiated inbound payload type to its clock/kind/MID so the reception stats can seed an inbound
    // source's exact §A.8 clock (and attribute it to a track) by matching the first packet's payload type — the
    // inbound SSRC is the remote's choice, unknown ahead of time. Audio uses its negotiated codec clock; video
    // uses 90 kHz (RFC 3551 §5). The RFC 4733 telephone-event PT shares the audio clock but is DTMF, not media —
    // it is left out (no inbound reception stream is attributed to it).
    private static IReadOnlyDictionary<byte, BundledInboundClockDescriptor> BuildInboundClockMap(
        BundledMediaSessionOptions options)
    {
        var map = new Dictionary<byte, BundledInboundClockDescriptor>
        {
            [options.Audio.PayloadType] = new BundledInboundClockDescriptor(
                options.Audio.ClockRate > 0 ? (uint)options.Audio.ClockRate : 0u,
                BundledStreamKind.Audio,
                options.Audio.Mid),
        };
        if (options.Video is { } video)
        {
            // A shared video PT can already be present (e.g. audio and video negotiated the same number is not
            // possible in practice, but guard anyway); the video entry wins for the video MID.
            map[video.PayloadType] = new BundledInboundClockDescriptor(VideoRtpClockRate, BundledStreamKind.Video, video.Mid);
        }

        return map;
    }

    // Maps each of our local sending SSRCs to the track (MID + kind) it belongs to, so a per-SSRC outbound
    // quality snapshot (RTT/loss keyed per our sending SSRC) can be attributed to a stream. Audio SSRC → audio
    // MID; a single video SSRC or each simulcast encoding's SSRC → video MID.
    private static IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> BuildOutboundStreamIdentity(
        BundledMediaSessionOptions options)
    {
        var map = new Dictionary<uint, BundledOutboundStreamIdentity>
        {
            [options.Audio.Ssrc] = new BundledOutboundStreamIdentity(options.Audio.Mid, BundledStreamKind.Audio),
        };
        if (options.Video is { } video)
        {
            if (video.Encodings.Count > 0)
            {
                foreach (var encoding in video.Encodings)
                    map[encoding.Ssrc] = new BundledOutboundStreamIdentity(video.Mid, BundledStreamKind.Video);
            }
            else
            {
                map[video.Ssrc] = new BundledOutboundStreamIdentity(video.Mid, BundledStreamKind.Video);
            }
        }

        return map;
    }

    private static BundledOutboundTrack BuildOutboundTrack(BundledMediaSessionOptions options, BundledTrackConfig track) =>
        new(track.Ssrc, track.PayloadType, track.SamplesPerPacket,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, options.MidExtensionId, track.Mid),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: track.VideoCodecName is null ? (uint)Math.Max(0, track.ClockRate) : VideoRtpClockRate);

    // One simulcast encoding's outbound stream: its own SSRC, the shared video payload type, and a stamper
    // that marks every packet with the MID and this encoding's RID (RFC 8852). Video packets carry an
    // explicit frame timestamp, so the timestamp cursor never advances (samplesPerPacket: 0).
    private static BundledOutboundTrack BuildEncodingTrack(
        BundledMediaSessionOptions options, string mid, uint ssrc, byte payloadType, string rid, byte ridExtensionId) =>
        new(ssrc, payloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                transportWideCcExtensionId: null, options.MidExtensionId, mid, ridExtensionId, rid),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: VideoRtpClockRate);

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>The local audio track's synchronisation source.</summary>
    public uint AudioSsrc => _audioSsrc;

    /// <summary>Whether this bundle carries a video track.</summary>
    public bool HasVideo => _video is not null;

    /// <summary>
    /// Whether outbound audio is sent. False when the negotiated directions do not carry audio from this peer
    /// to the remote (a send-only/inactive remote answer, or a local side that does not send); the audio
    /// m-line still anchors the transport and inbound audio is still received.
    /// </summary>
    public bool AudioSendEnabled => _audioSendEnabled;

    /// <summary>Whether the video track sends multiple simulcast encodings (RFC 8853).</summary>
    public bool VideoIsSimulcast => _video?.IsSimulcast ?? false;

    /// <summary>The configured simulcast <c>a=rid</c> layer ids, or empty when not simulcasting.</summary>
    public IReadOnlyCollection<string> VideoSendRids => _video?.SendRids ?? [];

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
        _ice.AddRelayLocalCandidate(binding.RelaySend);
        // Retain the binding so a later relay-pair nomination can ChannelBind + switch the transport.
        Volatile.Write(ref _relayBinding, binding);

        // Keep the adopted allocation alive. Started here (idempotent) so an adoption that lands after StartAsync
        // still runs the keepalive; the StartAsync start covers the pre-start case. Starting before the transport
        // receive loop is up is safe — the first refresh is roughly half the allocation lifetime away.
        Volatile.Write(ref _relayKeepAlive, binding.KeepAlive);
        binding.KeepAlive?.Start();
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

    /// <summary>Point-in-time transport counters aggregated from the outbound and inbound pipelines.</summary>
    public BundledMediaStats SnapshotStats() => new(
        _outbound.PacketsSent, _outbound.BytesSent, _outbound.SuppressedSends,
        _inbound.RtpPacketsReceived, _inbound.RtpBytesReceived, _inbound.DroppedDatagrams,
        _video?.FramesReceived, _video?.KeyFrames);

    /// <summary>
    /// Point-in-time derived quality: the RTCP outbound metrics (RFC 3550 §6.4.1 — round-trip time and the loss
    /// the peer reports on our media, both <see langword="null"/> until the peer echoes a matching report) folded
    /// together with our own local receive-side interarrival jitter (RFC 3550 §A.8, <see langword="null"/> until
    /// an inbound clock rate is established).
    /// </summary>
    public BundledMediaQuality SnapshotQuality() =>
        _outboundQuality.Snapshot() with { JitterMs = _receptionStats.SnapshotJitterMs() };

    /// <summary>
    /// Point-in-time derived quality per media stream (CF-004f). Two families of metric are folded together by
    /// MID:
    /// <list type="bullet">
    /// <item><description>
    /// RTT and the loss the peer reports on our media (RFC 3550 §6.4.1) are per <em>our sending</em> SSRC — each
    /// is attributed to its track (audio/video MID) via the negotiated outbound SSRC map. A simulcast MID folds
    /// its encodings' SSRCs into one video entry, taking the worst RTT/loss across them.
    /// </description></item>
    /// <item><description>
    /// The local receive-side interarrival jitter (RFC 3550 §A.8) is per <em>remote inbound</em> SSRC — attributed
    /// to a track by matching the first packet's payload type (audio PT → audio, video PT → video). An inbound
    /// source whose payload type was not negotiated, or seen only via an RTCP SR, has an unknown kind and is
    /// reported under its own SSRC with a null MID.
    /// </description></item>
    /// </list>
    /// The two directions do not share an SSRC (ours vs the remote's), so an entry carries RTT/loss (outbound) or
    /// jitter (inbound) depending on which direction it describes; a MID with both directions active folds them
    /// into one entry. Every metric is <see langword="null"/> until it is available.
    /// </summary>
    public IReadOnlyList<BundledStreamQuality> SnapshotStreamQuality()
    {
        // Fold outbound (per our sending SSRC) and inbound (per remote SSRC) into per-stream entries. Streams with
        // a known MID are keyed by MID so both directions of a track land in one entry; an unknown-kind inbound
        // source (no negotiated PT match) is keyed by its own SSRC so it is still surfaced, honestly, on its own.
        var byMid = new Dictionary<string, BundledStreamQualityAccumulator>(StringComparer.Ordinal);
        var unkeyed = new List<BundledStreamQuality>();

        foreach (var outbound in _outboundQuality.SnapshotPerSsrc())
        {
            if (!_outboundStreamIdentity.TryGetValue(outbound.Ssrc, out var identity))
                continue; // a report about an SSRC we do not send (should not happen) — do not fabricate a stream.

            var acc = GetOrAddMid(byMid, identity.Mid, identity.Kind, outbound.Ssrc);
            acc.MergeOutbound(outbound.RoundTripTimeMs, outbound.RemotePacketLossFraction);
        }

        foreach (var inbound in _receptionStats.SnapshotJitterMsPerSsrc())
        {
            if (inbound.Mid is { } mid)
            {
                var acc = GetOrAddMid(byMid, mid, inbound.Kind, inbound.Ssrc);
                acc.MergeInboundJitter(inbound.JitterMs);
            }
            else
            {
                // No MID resolvable (unmapped payload type / SR-only source): surface it on its own SSRC with the
                // kind we could derive — the honest limit of inbound remote-SSRC attribution.
                unkeyed.Add(new BundledStreamQuality(
                    Mid: null, inbound.Ssrc, inbound.Kind, PacketLoss: null, JitterMs: inbound.JitterMs, RoundTripTimeMs: null));
            }
        }

        var result = new List<BundledStreamQuality>(byMid.Count + unkeyed.Count);
        foreach (var acc in byMid.Values)
            result.Add(acc.ToStreamQuality());
        result.AddRange(unkeyed);
        return result;
    }

    private static BundledStreamQualityAccumulator GetOrAddMid(
        Dictionary<string, BundledStreamQualityAccumulator> byMid, string mid, BundledStreamKind kind, uint ssrc)
    {
        if (!byMid.TryGetValue(mid, out var acc))
        {
            acc = new BundledStreamQualityAccumulator(mid, ssrc, kind);
            byMid[mid] = acc;
        }

        return acc;
    }

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
    /// Sends one out-of-band DTMF tone as an RFC 4733 telephone-event burst on the audio track: an event-start
    /// packet (marker set, half the duration) followed by two end-of-event packets (E-bit set, full duration —
    /// the second a reliability retransmission per RFC 4733 §2.5.1.4), all sharing one RTP timestamp on the
    /// telephone-event payload type. Fails closed like all bundle sends — suppressed until the DTLS handshake
    /// keys the transport (never leaves as plaintext).
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

        var payloadType = _telephoneEventPayloadType
            ?? throw new InvalidOperationException("RTP telephone-event (DTMF) was not negotiated for this WebRTC session.");

        var durationRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(durationMs, _telephoneEventClockRate);
        var startDurationRtpUnits = (ushort)Math.Max(1, durationRtpUnits / 2);
        // The event shares the audio stream's timestamp clock (RFC 4733 §2.1): stamp the whole burst with the
        // audio track's current timestamp cursor, without advancing it (SendTimestampedAsync leaves it be).
        var eventTimestamp = _outbound.GetTrackTimestamp(_audioMid);

        var startPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: false, durationRtpUnits: startDurationRtpUnits);
        var endPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: true, durationRtpUnits: durationRtpUnits);

        await _outbound.SendTimestampedAsync(
            _audioMid, startPayload, marker: true, payloadType: (byte)payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _outbound.SendTimestampedAsync(
            _audioMid, endPayload, marker: false, payloadType: (byte)payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // RFC 4733 §2.5.1.4 reliability recommendation: repeat the final (end-of-event) packet.
        await _outbound.SendTimestampedAsync(
            _audioMid, endPayload, marker: false, payloadType: (byte)payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Packetises and sends one encoded video frame on the (non-simulcast) video track.</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track, or it is simulcast.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video is { } video
            ? video.SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>Packetises and sends one encoded video frame on a simulcast <paramref name="rid"/> layer (RFC 8853).</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video is { } video
            ? video.SendFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>
    /// Tears the session down: stops ICE and DTLS (closing the association, zeroing keys) before
    /// disposing the video track and finally the transport (which stops the receive loop and the socket).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
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
        // Stop the periodic Sender Reports before the transport it rides is torn down (its SRTCP send goes
        // through the transport), and before DTLS zeroes the outbound SRTCP key.
        await _rtcpReporter.DisposeAsync().ConfigureAwait(false);
        await _dtls.DisposeAsync().ConfigureAwait(false);
        _video?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
