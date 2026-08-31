using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using Microsoft.Extensions.Logging;

using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Pure composition helpers a <see cref="BundledMediaSession"/> uses to map its negotiated
/// <see cref="BundledMediaSessionOptions"/> onto the collaborators it wires up: the inbound clock/kind/MID
/// map (per payload type), the outbound SSRC→track identity map, and the per-video-m-line outbound senders
/// and <see cref="BundledVideoTrack"/> (P2b: N video tracks, RFC 8843 §9). Extracted so the session stays a
/// wiring/lifecycle unit under the 1000-line rule; these are side-effect-free apart from registering the
/// video senders on the passed-in outbound pipeline.
/// </summary>
internal static class BundledMediaSessionComposition
{
    /// <summary>
    /// The RTP clock rate used for the SR RTP-timestamp extrapolation of video (CF-004e): the fixed 90 kHz
    /// RTP clock (RFC 3551 §5). The bundle video track config carries no per-codec rate, and every supported
    /// video codec (H.264/VP8) runs at 90 kHz. Audio uses its own negotiated codec clock.
    /// </summary>
    public const uint VideoRtpClockRate = 90000;

    /// <summary>
    /// Maps each negotiated inbound payload type to its clock/kind/MID so the reception stats can seed an
    /// inbound source's exact §A.8 clock (and attribute it to a track) by matching the first packet's payload
    /// type — the inbound SSRC is the remote's choice, unknown ahead of time. Audio uses its negotiated codec
    /// clock; each video PT uses 90 kHz (RFC 3551 §5). The RFC 4733 telephone-event PT shares the audio clock
    /// but is DTMF, not media, so it is left out (no inbound reception stream is attributed to it).
    /// </summary>
    /// <summary>
    /// The callbacks a bundle's keying and reporting layer raises back onto the owning session: transport
    /// lifecycle, ICE consent, and the two nomination hooks. Passed as one record so the composition can build
    /// that layer without reaching for the session's events, which only the session may invoke.
    /// </summary>
    /// <param name="HandshakeFailed">The DTLS handshake did not complete.</param>
    /// <param name="Connected">DTLS installed the SRTP keys; media can flow.</param>
    /// <param name="PeerClosed">The peer closed the DTLS association.</param>
    /// <param name="MediaConsentLost">ICE consent freshness expired (RFC 7675).</param>
    /// <param name="MediaConnectivityDegraded">A transient consent miss.</param>
    /// <param name="MediaConnectivityRecovered">Consent recovered after a degrade.</param>
    /// <param name="PairNominated">A pair won the connectivity checks; the transport and DTLS follow it.</param>
    /// <param name="RelayPairNominated">The nominated pair is relayed, so the data path must switch to ChannelData.</param>
    internal sealed record BundledMediaKeyingCallbacks(
        Action HandshakeFailed,
        Action Connected,
        Action PeerClosed,
        Action MediaConsentLost,
        Action MediaConnectivityDegraded,
        Action MediaConnectivityRecovered,
        Action<System.Net.IPEndPoint> PairNominated,
        Action<System.Net.IPEndPoint> RelayPairNominated);

    /// <summary>
    /// The parts that key the bundle and report on it: congestion control, inbound RTCP fan-out, the DTLS
    /// association, the ICE agent, and the periodic Sender Reports.
    /// </summary>
    /// <param name="Congestion">The transport-wide congestion plane, or null when transport-cc was not negotiated.</param>
    /// <param name="RtcpDispatcher">Decodes inbound RTCP compounds and fans them out.</param>
    /// <param name="Dtls">The one DTLS association keying every track.</param>
    /// <param name="Ice">The one ICE agent keeping the group alive.</param>
    /// <param name="RtcpReporter">The periodic Sender Reports for the active outbound streams.</param>
    internal sealed record BundledMediaKeyingPlane(
        BundledCongestionPlane? Congestion,
        BundledInboundRtcpDispatcher RtcpDispatcher,
        BundledDtlsKeying Dtls,
        BundledIceControl Ice,
        BundledRtcpReporter RtcpReporter);

    /// <summary>
    /// Builds the keying and reporting layer on an assembled data path. Everything here needs the transport and
    /// the pipelines to already exist, which is why it is a second step rather than part of
    /// <see cref="BuildDataPath"/>.
    /// </summary>
    /// <param name="options">The negotiated bundle options.</param>
    /// <param name="dataPath">The assembled data path these parts ride.</param>
    /// <param name="handshaker">Runs the DTLS-SRTP handshake.</param>
    /// <param name="certificate">This endpoint's DTLS certificate.</param>
    /// <param name="callbacks">What this layer raises back onto the session.</param>
    /// <param name="loggerFactory">Builds each part's logger.</param>
    /// <param name="logger">The owning session's logger.</param>
    public static BundledMediaKeyingPlane BuildKeyingPlane(
        BundledMediaSessionOptions options,
        BundledMediaDataPath dataPath,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        BundledMediaKeyingCallbacks callbacks,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        // Transport-wide congestion control (transport-cc), one plane per bundle. Only when the
        // a=extmap was negotiated (so the transport actually stamps a transport-wide sequence) — otherwise the
        // plane stays off. See BundledCongestionPlane: it wires the sender-side controller to PacketSent and the
        // receive-side feedback sender to inbound RTP; OnControlPacketReceived fans decoded feedback into it.
        var congestion = BundledMediaSessionComposition.BuildCongestionPlane(
            options, dataPath.Outbound, dataPath.Inbound, dataPath.RtcpCodec, loggerFactory);

        // Inbound RTCP decode + fan-out (RFC 3550 §6.4.1 / RFC 4585 / transport-cc): built now that the video set and
        // congestion plane exist. Invoked only from the receive loop (subscribed on dataPath.Inbound above), which starts
        // in StartAsync, so this field is always assigned before the first dispatch.
        var dispatcher = new BundledInboundRtcpDispatcher(
            dataPath.RtcpCodec, dataPath.ReceptionStats, dataPath.OutboundQuality, dataPath.Video, congestion, logger);

        // One shared DTLS association keys every track; one shared ICE agent keeps the group alive.
        var dtls = new BundledDtlsKeying(
            options.DtlsIsClient, options.RemoteEndPoint, options.RemoteFingerprint,
            handshaker, certificate, dataPath.Inbound, dataPath.Outbound, dataPath.Transport,
            onHandshakeFailed: callbacks.HandshakeFailed, loggerFactory,
            onKeysInstalled: callbacks.Connected,
            onPeerClosed: callbacks.PeerClosed);

        var ice = new BundledIceControl(
            options.Ice, dataPath.Inbound, dataPath.Transport.SendToAsync, loggerFactory,
            onConsentLost: callbacks.MediaConsentLost,
            onConnectivityDegraded: callbacks.MediaConnectivityDegraded,
            onConnectivityRecovered: callbacks.MediaConnectivityRecovered,
            // A nominated ICE pair (RFC 8445 §8) becomes the transport's send target AND the DTLS remote,
            // so the DTLS handshake's inbound source filter follows the connectivity-checked pair.
            onPairNominated: callbacks.PairNominated,
            // The relay send path (when a TURN allocation was gathered) becomes the ICE agent's relay local
            // candidate — checked alongside the direct one, direct-preferred by pair priority.
            relaySend: dataPath.RelayBinding?.RelaySend,
            // A nominated relay pair additionally switches the transport onto the relay data path (ChannelBind).
            onRelayPairNominated: callbacks.RelayPairNominated,
            // Reconciles the hairpin case, where a peer arrives through a TURN server on this machine and
            // its source address is a local interface rather than the relay address it advertised.
            remoteEndPointTranslator: options.RemoteEndPointTranslator,
            // Reaches a STUN server as-is in either transport mode, so the reflexive address can be re-probed
            // on a live transport after an ICE restart without giving up the socket.
            sendUnframed: dataPath.Transport.SendUnframedAsync);

        // Periodic RTCP Sender Reports for the active outbound streams (RFC 3550 §6.4): reads the outbound
        // pipeline's per-SSRC SR counters and sends over its fail-closed SRTCP send path. The CNAME mirrors the
        // SIP-path monitor so both report the same canonical name. Started in StartAsync (early ticks are
        // suppressed until DTLS installs the outbound SRTCP key); disposed before the transport it rides.
        var reporter = new BundledRtcpReporter(
            dataPath.Outbound.SnapshotSenderReports,
            dataPath.ReceptionStats.SnapshotReportBlocks,
            options.Audio.Ssrc,
            dataPath.Outbound.SendRtcpAsync,
            dataPath.RtcpCodec,
            // Opaque per-session CNAME (RFC 7022) — never the machine name (privacy/correlation); overridable.
            options.Cname ?? RtcpCname.NewOpaque(),
            loggerFactory,
            // Record each emitted SR's LSR + send instant so a peer's echoed report yields RTT (RFC 3550 §6.4.1).
            onSenderReportSent: dataPath.OutboundQuality.RecordLocalSenderReport);

        return new BundledMediaKeyingPlane(congestion, dispatcher, dtls, ice, reporter);
    }

    /// <summary>
    /// The parts of a bundle's data path: everything a packet passes through on its way in or out, built in
    /// the one order their dependencies allow.
    /// </summary>
    /// <param name="Router">Demultiplexes inbound RTP to the owning track.</param>
    /// <param name="ReceptionStats">Per-SSRC inbound statistics feeding the periodic RTCP report blocks.</param>
    /// <param name="OutboundQuality">Derives RTT and peer-observed loss from the reception blocks the peer returns.</param>
    /// <param name="RtcpCodec">Shared RTCP codec for the dispatcher, congestion plane and reporter.</param>
    /// <param name="Inbound">The inbound pipeline the transport feeds.</param>
    /// <param name="Transport">The shared socket every track rides.</param>
    /// <param name="Outbound">The outbound pipeline, one sender per m-line.</param>
    /// <param name="AudioTracks">The additional inbound audio m-lines (4.7.0).</param>
    /// <param name="Video">One track per negotiated video m-line (P2b).</param>
    /// <param name="RelayBinding">The TURN relay wiring when an allocation was gathered, else null.</param>
    internal sealed record BundledMediaDataPath(
        BundledTrackRouter Router,
        BundledInboundReceptionStats ReceptionStats,
        BundledOutboundQualityTracker OutboundQuality,
        RtcpPacketCodec RtcpCodec,
        BundledInboundPipeline Inbound,
        BundledMediaTransport Transport,
        BundledOutboundPipeline Outbound,
        BundledAudioTrackSet AudioTracks,
        BundledVideoTrackSet Video,
        RelayIceBinding? RelayBinding);

    /// <summary>
    /// Builds the bundle's data path. The order is not stylistic: the router has to exist before the inbound
    /// pipeline that feeds it, the pipeline before the transport that drives it, the transport before the relay
    /// binding that is built from its send path, and all of those before the tracks that register on them.
    /// </summary>
    /// <param name="options">The negotiated bundle options.</param>
    /// <param name="raiseAudioReceived">Receives inbound RTP on the primary audio MID.</param>
    /// <param name="onControlPacketReceived">Receives decrypted inbound RTCP compounds.</param>
    /// <param name="wireVideoTrackEvents">Wires one video track's frame/key-frame events (mid, track, isPrimary).</param>
    /// <param name="raiseAudioTrackFrame">Raises the mid-tagged inbound frame for an additional audio track.</param>
    /// <param name="loggerFactory">Builds each part's logger.</param>
    /// <param name="logger">The owning session's logger.</param>
    public static BundledMediaDataPath BuildDataPath(
        BundledMediaSessionOptions options,
        Action<RtpPacket> raiseAudioReceived,
        Action<byte[]> onControlPacketReceived,
        Action<string, BundledVideoTrack, bool> wireVideoTrackEvents,
        Action<string, RtpPacket> raiseAudioTrackFrame,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        // Inbound: demux the shared socket by the negotiated m-lines' payload types, route each MID.
        var router = new BundledTrackRouter(
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, BuildPayloadTypesByMid(options), options.RidExtensionId));
        router.RegisterTrack(options.Audio.Mid, raiseAudioReceived);

        // Per-SSRC inbound reception statistics (RFC 3550 §6.4.1) feed the periodic RTCP report blocks. The
        // negotiated clock/kind is applied per inbound source by matching the first packet's payload type (the
        // inbound SSRC is the remote's choice), so audio gets its exact §A.8 clock and video gets 90 kHz
        // regardless of arrival order, and each source is attributed to its track (CF-004f).
        var receptionStats = new BundledInboundReceptionStats(clockByPayloadType: BuildInboundClockMap(options));
        // Consumes the reception blocks the peer returns about our outbound streams to derive RTT and the loss
        // the peer sees (RFC 3550 §6.4.1): fed by the reporter's SR send instants and by inbound RR/SR blocks.
        var outboundQuality = new BundledOutboundQualityTracker();

        var inbound = new BundledInboundPipeline(
            router, new RtpPacketCodec(), loggerFactory.CreateLogger<BundledInboundPipeline>(), receptionStats);
        // Inbound Sender Reports carry the LSR the peer needs echoed back: decode each decrypted compound and
        // record every SR's middle-32 NTP bits + arrival time per sender SSRC (RFC 3550 §6.4.1).
        inbound.ControlPacketReceived += onControlPacketReceived;

        var transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = options.LocalEndPoint, RemoteEndPoint = options.RemoteEndPoint },
            inbound, loggerFactory.CreateLogger<BundledMediaTransport>(), options.PreBoundSocket);

        // A relay ICE local candidate rides the same shared socket. Now that the socket exists, the injected
        // (TURN-aware) factory builds the indication channel + control transactor + relay send path; the
        // transport unwraps relayed inbound datagrams and feeds control responses (SetIndicationRelay), and the
        // relay send path becomes the ICE agent's relay candidate. Null (no gathered allocation) leaves the
        // transport direct-only.
        //
        // Unframed send: the relay control stack (control transactions + Send indications) is addressed to the
        // relay server itself and must reach it raw in both modes — never framed as ChannelData once the
        // transport enters relay mode.
        var relayBinding = options.RelayIceBindingFactory?.Invoke(transport.SendUnframedAsync);
        if (relayBinding is not null)
            transport.SetIndicationRelay(relayBinding.Indication, relayBinding.OnControl);

        // Outbound: a per-track sender for each m-line, stamping its MID (and, when negotiated, the one
        // transport-wide-cc sequence the pipeline advances across all tracks).
        var outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), transport, loggerFactory.CreateLogger<BundledOutboundPipeline>(),
            stampsTransportCc: options.TransportWideCcExtensionId is not null);
        outbound.RegisterTrack(options.Audio.Mid, BuildOutboundTrack(options, options.Audio));

        var (audioTracks, video) = BuildTrackSets(
            options, router, outbound, wireVideoTrackEvents, raiseAudioTrackFrame, loggerFactory, logger);

        return new BundledMediaDataPath(
            router, receptionStats, outboundQuality, new RtcpPacketCodec(),
            inbound, transport, outbound, audioTracks, video, relayBinding);
    }

    /// <summary>
    /// The payload-type→MID map the inbound demultiplexer is built from: one entry per negotiated m-line.
    /// </summary>
    /// <remarks>
    /// A payload type shared across several tracks (two same-codec video streams both using PT 96, say) is
    /// dropped from this map by the demultiplexer factory, so those packets route by the MID header extension
    /// (RFC 9143) instead of being guessed from an ambiguous PT. What this method enforces is the other half:
    /// two m-lines may never claim the same MID — that would make the routing key itself ambiguous, and no
    /// header extension could disambiguate it afterwards.
    /// </remarks>
    /// <param name="options">The negotiated bundle options.</param>
    /// <exception cref="ArgumentException">Two m-lines share a MID.</exception>
    public static Dictionary<string, IReadOnlyCollection<int>> BuildPayloadTypesByMid(BundledMediaSessionOptions options)
    {
        var payloadTypesByMid = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
        {
            [options.Audio.Mid] = new[] { (int)options.Audio.PayloadType },
        };

        foreach (var video in options.VideoTracks)
        {
            if (!payloadTypesByMid.TryAdd(video.Mid, new[] { (int)video.PayloadType }))
                throw new ArgumentException(
                    $"Duplicate video MID '{video.Mid}' in the bundle options.", nameof(options));
        }

        // A MID colliding with the primary audio or a video m-line is rejected here too.
        foreach (var audio in options.AdditionalAudioTracks)
        {
            if (!payloadTypesByMid.TryAdd(audio.Mid, new[] { (int)audio.PayloadType }))
                throw new ArgumentException(
                    $"Duplicate audio MID '{audio.Mid}' in the bundle options.", nameof(options));
        }

        return payloadTypesByMid;
    }

    /// <summary>
    /// Builds the bundle's track sets on an existing router and outbound pipeline: the additional inbound audio
    /// m-lines (4.7.0) and one <see cref="BundledVideoTrack"/> per negotiated video m-line (P2b).
    /// </summary>
    /// <remarks>
    /// Registration order inside the video loop is deliberate and mirrors the live add path: the track's events
    /// are wired before its router sink exists, so a packet arriving for a MID whose sink is not yet registered
    /// is cleanly dropped and counted rather than delivered to a half-built track. Per-SSRC SRTP keeps two
    /// same-codec video streams independent, which is why they can share a payload type at all.
    /// </remarks>
    /// <param name="options">The negotiated bundle options.</param>
    /// <param name="router">The inbound router each track registers its sink on.</param>
    /// <param name="outbound">The outbound pipeline each track registers its sender(s) on.</param>
    /// <param name="wireVideoTrackEvents">Wires one video track's frame/key-frame events (mid, track, isPrimary).</param>
    /// <param name="raiseAudioTrackFrame">Raises the mid-tagged inbound audio frame for an additional track.</param>
    /// <param name="loggerFactory">Builds each track's logger.</param>
    /// <param name="logger">The owning session's logger, for the track sets themselves.</param>
    public static (BundledAudioTrackSet Audio, BundledVideoTrackSet Video) BuildTrackSets(
        BundledMediaSessionOptions options,
        BundledTrackRouter router,
        BundledOutboundPipeline outbound,
        Action<string, BundledVideoTrack, bool> wireVideoTrackEvents,
        Action<string, RtpPacket> raiseAudioTrackFrame,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        // Empty list → empty set, which keeps the single-audio path byte-identical to what it was before
        // additional audio m-lines existed.
        var audio = options.AdditionalAudioTracks.Count > 0
            ? new BundledAudioTrackSet(options, router, outbound, raiseAudioTrackFrame, logger)
            : new BundledAudioTrackSet(outbound, logger);

        var builtVideo = new List<(string Mid, BundledVideoTrack Track)>(options.VideoTracks.Count);
        foreach (var video in options.VideoTracks)
        {
            var track = BuildVideoTrack(options, video, outbound, loggerFactory);
            wireVideoTrackEvents(video.Mid, track, builtVideo.Count == 0);
            router.RegisterTrack(video.Mid, track.OnRtpPacket);
            builtVideo.Add((video.Mid, track));
        }

        return (audio, builtVideo.Count > 0 ? new BundledVideoTrackSet(builtVideo) : new BundledVideoTrackSet());
    }

    public static IReadOnlyDictionary<byte, BundledInboundClockDescriptor> BuildInboundClockMap(
        BundledMediaSessionOptions options)
    {
        var map = new Dictionary<byte, BundledInboundClockDescriptor>
        {
            [options.Audio.PayloadType] = new BundledInboundClockDescriptor(
                options.Audio.ClockRate > 0 ? (uint)options.Audio.ClockRate : 0u,
                BundledStreamKind.Audio,
                options.Audio.Mid),
        };
        // Each additional inbound audio m-line (4.7.0: N audio tracks) resolves to its own negotiated codec
        // clock / Audio, attributed to its MID. When two audio tracks share a payload type (two same-codec
        // streams), a single PT-keyed clock entry cannot carry both MIDs — the primary already holds that PT and
        // wins the key; the extra track keeps the same Audio/clock, so only the per-track MID attribution of
        // inbound jitter is affected for a shared PT (routing itself is by MID, RFC 9143, unaffected).
        foreach (var audio in options.AdditionalAudioTracks)
        {
            if (!map.ContainsKey(audio.PayloadType))
                map[audio.PayloadType] = new BundledInboundClockDescriptor(
                    audio.ClockRate > 0 ? (uint)audio.ClockRate : 0u, BundledStreamKind.Audio, audio.Mid);
        }
        // Each video PT resolves to 90 kHz / Video. When two video tracks share a payload type (two same-codec
        // streams), a single PT-keyed clock entry cannot carry both MIDs — the first video track's MID is kept
        // (both are Video/90 kHz, so only the per-track MID attribution of inbound jitter is affected, and only
        // for a shared PT; distinct PTs attribute exactly). The primary is listed first, so it wins the shared
        // key — the honest limit of PT-based inbound attribution, not a media-path concern (routing is by MID).
        foreach (var video in options.VideoTracks)
        {
            if (!map.ContainsKey(video.PayloadType))
                map[video.PayloadType] = new BundledInboundClockDescriptor(VideoRtpClockRate, BundledStreamKind.Video, video.Mid);
        }

        return map;
    }

    /// <summary>
    /// Maps each of our local sending SSRCs to the track (MID + kind) it belongs to, so a per-SSRC outbound
    /// quality snapshot (RTT/loss keyed per our sending SSRC) can be attributed to a stream. Audio SSRC → audio
    /// MID; each video track's single SSRC (or each simulcast encoding's SSRC) → that track's MID. SSRCs are
    /// bundle-wide-distinct (RFC 3550 §8.1), so every entry maps cleanly across N video tracks (P2b).
    /// <para>
    /// Mutable and concurrent by design (#161 P2-11): a live-added track contributes its SSRCs here and a
    /// deactivated one drops them, so the attribution follows the bundle instead of freezing at construction.
    /// The metrics snapshot reads it while the control plane mutates it.
    /// </para>
    /// </summary>
    public static ConcurrentDictionary<uint, BundledOutboundStreamIdentity> BuildOutboundStreamIdentity(
        BundledMediaSessionOptions options)
    {
        var map = new ConcurrentDictionary<uint, BundledOutboundStreamIdentity>();
        map[options.Audio.Ssrc] = new BundledOutboundStreamIdentity(options.Audio.Mid, BundledStreamKind.Audio);
        foreach (var audio in options.AdditionalAudioTracks)
            map[audio.Ssrc] = new BundledOutboundStreamIdentity(audio.Mid, BundledStreamKind.Audio);
        foreach (var video in options.VideoTracks)
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

    /// <summary>
    /// Baut die Transport-CC-Ebene des Bundles (draft-holmer), oder <see langword="null"/>, wenn die
    /// <c>a=extmap</c> nicht ausgehandelt wurde und der Transport folglich keine transport-weite
    /// Sequenz stempelt.
    /// </summary>
    /// <remarks>
    /// #162 P2-3: Ein Bundle hat einen Transport, also auch eine Entscheidung über die RTCP-Form.
    /// Sobald auch nur eine Sektion kein <c>a=rtcp-rsize</c> ausgehandelt hat, sendet die Ebene
    /// Compounds — im Zweifel die Form, die jeder Empfänger annehmen muss (RFC 3550 §6.1).
    /// </remarks>
    public static BundledCongestionPlane? BuildCongestionPlane(
        BundledMediaSessionOptions options,
        BundledOutboundPipeline outbound,
        BundledInboundPipeline inbound,
        IRtcpPacketCodec rtcpCodec,
        ILoggerFactory loggerFactory)
    {
        if (options.TransportWideCcExtensionId is not { } transportCcExtensionId)
            return null;

        var reducedSizeRtcp = options.VideoTracks.Count > 0
                              && options.VideoTracks.All(v => v.ReducedSizeRtcp);

        return new BundledCongestionPlane(
            transportCcExtensionId, outbound, inbound, rtcpCodec, options.Audio.Ssrc, loggerFactory,
            reducedSizeRtcp);
    }

    public static BundledVideoTrack BuildVideoTrack(
        BundledMediaSessionOptions options, BundledTrackConfig video, BundledOutboundPipeline outbound, ILoggerFactory loggerFactory)
    {
        var codecName = video.VideoCodecName
            ?? throw new ArgumentException("A video track must name its codec.", nameof(options));

        // The track is built BEFORE anything is registered, and every registration this call makes is undone
        // if a later step throws (#161 P2-11). Registering first left a live outbound sender behind whenever
        // the track constructor rejected the config — sending on a MID with no track, holding its SSRCs and
        // its MID key, so even a corrected retry failed with "already registered".
        if (video.Encodings.Count > 0)
        {
            // Send-side simulcast (RFC 8853): one outbound RTP stream per a=rid layer under the shared
            // MID, each on its own SSRC with the negotiated RID header extension (RFC 8852) stamped.
            var ridExtensionId = options.RidExtensionId ?? throw new ArgumentException(
                "A simulcast video track needs a negotiated RID header-extension id.", nameof(options));

            var track = new BundledVideoTrack(
                video.Mid, codecName, video.PayloadType, video.Ssrc,
                video.RemoteSupportsNack, video.RemoteSupportsPli,
                video.Encodings.Select(e => e.Rid).ToArray(),
                outbound, options.VideoReorderDepth, loggerFactory,
                receiveRids: video.ReceiveRids,
                // End-to-end encrypted frames: resolve the opaque payload format instead of the clear-media one
                // (#223, ADR-068) — for every simulcast layer and receive lane of this track.
                opaqueFrames: video.OpaqueVideoFrames,
                dependencyDescriptorExtensionId: video.DependencyDescriptorExtensionId);

            var registered = new List<string?>(video.Encodings.Count);
            try
            {
                foreach (var encoding in video.Encodings)
                {
                    outbound.RegisterTrack(video.Mid, encoding.Rid,
                        BuildEncodingTrack(
                            options, video.Mid, encoding.Ssrc, video.PayloadType, encoding.Rid, ridExtensionId,
                            video.DependencyDescriptorExtensionId));
                    registered.Add(encoding.Rid);
                }
            }
            catch
            {
                // Undo exactly the layers this call registered — never a MID-wide sweep, which on the
                // (gated, unreachable) duplicate path would tear down someone else's live registration.
                foreach (var rid in registered)
                    outbound.UnregisterTrack(video.Mid, rid);
                track.Dispose();
                throw;
            }

            return track;
        }

        var plain = new BundledVideoTrack(
            video.Mid, codecName, video.PayloadType, video.Ssrc,
            video.RemoteSupportsNack, video.RemoteSupportsPli,
            outbound, options.VideoReorderDepth, loggerFactory,
            // RTX repair stream (RFC 4588): retain sent packets and resend on an inbound NACK. Wired for
            // the non-simulcast track only — per-encoding simulcast RTX is follow-up work. Its repair
            // SSRC is allocated bundle-wide-distinct by the factory (RFC 3550 §8.1).
            rtxPayloadType: video.RtxPayloadType,
            rtxSsrc: video.RtxSsrc,
            // The negotiated inbound RID allowlist (#161 P3-15); empty admits every RID, as before.
            receiveRids: video.ReceiveRids,
            // End-to-end encrypted frames: resolve the opaque payload format instead of the clear-media one
            // (#223, ADR-068), so neither half of this track reads the frame.
            opaqueFrames: video.OpaqueVideoFrames,
            // Key frame and layer from the RTP header when the peer negotiated the descriptor (#225).
            dependencyDescriptorExtensionId: video.DependencyDescriptorExtensionId);

        try
        {
            outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
        }
        catch
        {
            plain.Dispose();
            throw;
        }

        return plain;
    }

    /// <summary>
    /// Builds a single (audio or plain-video) outbound track: its SSRC, payload type, samples-per-packet, and
    /// a header stamper for the MID (and transport-wide-cc, when negotiated). Audio uses its negotiated codec
    /// clock; video uses the fixed 90 kHz RTP clock (RFC 3551 §5).
    /// </summary>
    public static BundledOutboundTrack BuildOutboundTrack(BundledMediaSessionOptions options, BundledTrackConfig track) =>
        new(track.Ssrc, track.PayloadType, track.SamplesPerPacket,
            new RtpOutboundHeaderExtensionStamper(
                options.TransportWideCcExtensionId, options.MidExtensionId, track.Mid,
                dependencyDescriptorExtensionId: track.DependencyDescriptorExtensionId),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: track.VideoCodecName is null ? (uint)Math.Max(0, track.ClockRate) : VideoRtpClockRate);

    /// <summary>
    /// Folds the per-SSRC outbound quality (RTT + the loss the peer reports on our media, per <em>our sending</em>
    /// SSRC) and the per-SSRC inbound interarrival jitter (per <em>remote</em> SSRC) into one per-stream list
    /// (CF-004f). Streams with a known MID are keyed by MID so both directions of a track land in one entry (a
    /// simulcast MID folds its encodings' SSRCs into one video entry, taking the worst RTT/loss); an inbound
    /// source whose payload type was not negotiated has no MID and is surfaced on its own SSRC with a null MID —
    /// the honest limit of inbound remote-SSRC attribution. The two directions do not share an SSRC, so an entry
    /// carries RTT/loss (outbound) or jitter (inbound); a MID active in both folds them together. Pure — no
    /// session state beyond the three snapshots the session passes in.
    /// </summary>
    public static IReadOnlyList<BundledStreamQuality> FoldStreamQuality(
        IReadOnlyList<BundledOutboundSsrcQuality> outboundPerSsrc,
        IReadOnlyList<BundledInboundSsrcJitter> inboundJitterPerSsrc,
        IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> outboundStreamIdentity)
    {
        var byMid = new Dictionary<string, BundledStreamQualityAccumulator>(StringComparer.Ordinal);
        var unkeyed = new List<BundledStreamQuality>();

        foreach (var outbound in outboundPerSsrc)
        {
            if (!outboundStreamIdentity.TryGetValue(outbound.Ssrc, out var identity))
                continue; // a report about an SSRC we do not send (should not happen) — do not fabricate a stream.

            var acc = GetOrAddMid(byMid, identity.Mid, identity.Kind, outbound.Ssrc);
            acc.MergeOutbound(outbound.RoundTripTimeMs, outbound.RemotePacketLossFraction);
        }

        foreach (var inbound in inboundJitterPerSsrc)
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

    /// <summary>
    /// Emits one out-of-band DTMF tone as an RFC 4733 telephone-event burst on the primary audio track: an
    /// event-start packet (marker set, half the duration) followed by two end-of-event packets (E-bit, full
    /// duration — the second a reliability retransmission per RFC 4733 §2.5.1.4), all sharing one RTP timestamp on
    /// the telephone-event payload type. The event shares the audio stream's timestamp clock (RFC 4733 §2.1): the
    /// whole burst is stamped with the audio track's current cursor, and the event's full duration is reserved so
    /// the cursor advances past it — otherwise a following event (or media) reuses this timestamp and a receiver
    /// folds it into this event, dropping the repeated tone. Extracted from <see cref="BundledMediaSession"/> for
    /// the size limit; the caller has already validated the tone/duration and resolved the payload type.
    /// </summary>
    public static async ValueTask SendDtmfBurstAsync(
        BundledOutboundPipeline outbound, string audioMid, byte toneCode, int durationMs, int clockRate,
        byte payloadType, CancellationToken cancellationToken)
    {
        var durationRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(durationMs, clockRate);
        var startDurationRtpUnits = (ushort)Math.Max(1, durationRtpUnits / 2);
        var eventTimestamp = outbound.ReserveTrackTimestamp(audioMid, durationRtpUnits);

        var startPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: false, durationRtpUnits: startDurationRtpUnits);
        var endPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: true, durationRtpUnits: durationRtpUnits);

        await outbound.SendTimestampedAsync(
            audioMid, startPayload, marker: true, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await outbound.SendTimestampedAsync(
            audioMid, endPayload, marker: false, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // RFC 4733 §2.5.1.4 reliability recommendation: repeat the final (end-of-event) packet.
        await outbound.SendTimestampedAsync(
            audioMid, endPayload, marker: false, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // One simulcast encoding's outbound stream: its own SSRC, the shared video payload type, and a stamper
    // that marks every packet with the MID and this encoding's RID (RFC 8852). Video packets carry an
    // explicit frame timestamp, so the timestamp cursor never advances (samplesPerPacket: 0).
    private static BundledOutboundTrack BuildEncodingTrack(
        BundledMediaSessionOptions options, string mid, uint ssrc, byte payloadType, string rid, byte ridExtensionId,
        byte? dependencyDescriptorExtensionId = null) =>
        new(ssrc, payloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                options.TransportWideCcExtensionId, options.MidExtensionId, mid, ridExtensionId, rid,
                dependencyDescriptorExtensionId),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: VideoRtpClockRate);
}
