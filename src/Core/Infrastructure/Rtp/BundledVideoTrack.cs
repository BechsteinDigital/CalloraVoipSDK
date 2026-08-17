using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Carries one video m-line over a bundled transport (ADR-011 B4, RFC 8843): it bridges the video RTP
/// payload format (H.264 RFC 6184 / VP8 RFC 7741) to the bundle pipelines. Outbound, it packetises an
/// encoded frame and sends each payload through the <see cref="BundledOutboundPipeline"/> on the video
/// MID; inbound, it is the router sink for that MID — it reorders arriving packets and depacketises them
/// back into frames. The heavy lifting stays in the reused <see cref="IVideoPacketiser"/>,
/// <see cref="IVideoDepacketiser"/>, and <see cref="VideoReorderBuffer"/>; the transport (shared socket,
/// DTLS, ICE, SRTP) is the bundle's, so this track no longer needs its own <see cref="Session.RtpSession"/>.
/// </summary>
/// <remarks>
/// The receive path (<see cref="OnRtpPacket"/>) is single-consumer — the depacketiser and each reorder
/// window are stateful and not thread-safe, so they must be driven only from the bundle's single receive
/// loop, exactly as the single-stream video path drives it from the RTP receive loop. This single-consumer
/// guarantee is why the per-RID lane map (<c>_ridLayers</c>) is a plain dictionary. Sends are serialised
/// per encoding so a frame's packets never interleave with another frame's on the same RTP stream;
/// distinct simulcast encodings (distinct SSRCs) send independently.
/// <para>
/// Simulcast (RFC 8853): when built with encodings, the track sends N independent RTP streams under one
/// MID — one per <c>a=rid</c> layer, each on its own SSRC with the RID stamped per packet (RFC 8852).
/// Receive-side, each inbound RID is demultiplexed into its own reassembly lane (own depacketiser, reorder
/// window, and arrival-loss tracker) so interleaved encodings never corrupt each other's reassembly or
/// raise phantom NACKs; frames are surfaced on <see cref="FrameReceived"/> tagged with their RID. This is a
/// forwarding-only SFU demux — per-layer selection/BWE and per-encoding RTX are follow-up work.
/// </para>
/// <para>
/// RTX retransmission (RFC 4588) is wired for the non-simulcast track when an rtx payload type is negotiated:
/// it retains this stream's sent packets and, on an inbound Generic NACK, resends them on a separate repair
/// stream (own SSRC + rtx payload type, OSN-prefixed) over the shared transport. Per-encoding RTX on a
/// simulcast track is follow-up work; a simulcast track carries no repair stream.
/// </para>
/// </remarks>
internal sealed class BundledVideoTrack : IDisposable
{
    private readonly string _mid;
    private readonly BundledOutboundPipeline _outbound;
    private readonly ILogger<BundledVideoTrack> _logger;

    // This stream's primary outbound SSRC. Inbound RTCP feedback (PLI/FIR/NACK) names the media SSRC it is
    // about, and on a BUNDLE that is the only way to tell which track it belongs to.
    private readonly uint _localSsrc;

    // Retained so a simulcast RID lane can be built lazily with the same codec and reorder window as the
    // default lane (a browser stamps the RID extension only on the first packets of each encoding, so the
    // second/third encoding's lane is created on first sighting, not up front).
    private readonly string _codecName;
    private readonly int _reorderWindowDepth;

    // The negotiated Dependency Descriptor extension id (#225), or null when the peer did not accept it.
    private readonly byte? _dependencyDescriptorId;

    // #223/ADR-068: this track's frames are end-to-end encrypted (WebRTC Encoded Transform / SFrame, RFC 9605),
    // so no half of the payload format may read the frame. Retained because a lazily-built simulcast RID lane
    // resolves its own depacketiser later and must resolve the SAME policy — a lane that fell back to the
    // clear-media pair would reintroduce exactly the payload dependency this flag removes.
    private readonly bool _opaqueFrames;

    // The default inbound lane (RID null): the non-simulcast single stream, and — on a simulcast receive —
    // the base/primary encoding that carries no RID (or arrives before its RID is latched). Each simulcast
    // RID gets its own lane in _ridLayers so interleaved SSRCs never share reorder/loss state.
    private readonly BundledVideoInboundLayer _defaultLane;
    // The a=rid ids this peer negotiated to receive (RFC 8853), or empty when no receive simulcast was
    // negotiated. An allowlist, not a bound: the lane cap below is the DoS bound and stays either way.
    private readonly IReadOnlySet<string> _receiveRids;
    // Per-RID inbound lanes, built lazily on first RID sighting. A plain Dictionary — the receive path is
    // single-consumer (see the class remarks), so no concurrent map is needed; it is only ever mutated and
    // read from the bundle's single receive loop.
    private readonly Dictionary<string, BundledVideoInboundLayer> _ridLayers = new(StringComparer.Ordinal);

    // RTCP keyframe/loss feedback for this stream (RFC 4585/5104), mirroring the single-stream
    // VideoRtpStream: inbound PLI/FIR → KeyFrameRequested; detected inbound loss → outbound NACK/PLI.
    // Shared across all lanes: it names loss by the packet's SSRC, which is already per-encoding correct.
    private readonly VideoKeyFrameFeedback _keyFrameFeedback;
    // Cancels in-flight feedback sends when the track is disposed, so a NACK/PLI never races teardown.
    private readonly CancellationTokenSource _lifetimeCts = new();

    // The non-simulcast single stream (RID null), or null when this is a simulcast track.
    private readonly BundledVideoSendEncoding? _single;
    // The simulcast layers keyed by a=rid, or empty for a non-simulcast track.
    private readonly IReadOnlyDictionary<string, BundledVideoSendEncoding> _layers;

    // Inbound RTX recovery (RFC 4588 §4), non-simulcast only: when RTX was negotiated the router sink also
    // receives the peer's repair stream on the shared video MID (its rtx SSRC carries the same a=mid, RFC 9143),
    // so OnRtpPacket must split it out by payload type — decapsulate the OSN-prefixed repair packet and feed the
    // recovered original into the same reorder window a primary packet takes, letting a retransmit fill the gap
    // that prompted the NACK. Gated off (false) when RTX was not negotiated or on a simulcast track.
    private readonly bool _rtxConfigured;
    // The primary video payload type of this stream, used to reconstruct the original PT on decapsulation.
    private readonly byte _videoPayloadType;
    // The remote media SSRC, captured from the first primary inbound packet, stamped on recovered RTX packets.
    // Cosmetic only — the reorder buffer keys on sequence number and the depacketiser ignores SSRC (mirrors
    // VideoRtpStream): an RTX arriving before any primary stamps the recovered packet with 0.
    private uint _remoteMediaSsrc;

    // RTX retransmission (RFC 4588), non-simulcast only: retains this stream's sent packets so an inbound
    // Generic NACK can be answered by resending them on a separate repair stream (own SSRC + rtx payload type,
    // OSN-prefixed). All null when RTX was not negotiated (the retransmit callback stays a no-op) or on a
    // simulcast track (per-encoding RTX is follow-up work). The repair stream rides the bundle's shared
    // outbound SRTP context, which keys ROC/replay per SSRC — the fresh rtx SSRC needs no separate context.
    private readonly RtpRetransmissionBuffer? _retransmitBuffer;
    private readonly byte _rtxPayloadType;
    private readonly uint _rtxSsrc;
    private readonly Action<RtpPacket>? _retainSent;
    private int _rtxSequence;

    // RTP payload budget: MTU minus RTP/SRTP/extension overhead (mirrors the single-stream video path).
    private const int MaxRtpPayloadSize = 1200;

    // Track-wide inbound frame counters (aggregated across every lane), updated with Interlocked.
    private long _framesReceived;
    private long _keyFrames;
    private long _framesDropped;

    /// <summary>
    /// Raised with a reassembled encoded frame, its RTP timestamp, whether it is a key frame, and the
    /// simulcast <c>a=rid</c> the frame belongs to (RFC 8853) — <see langword="null"/> for the default
    /// (non-simulcast, or base RID-less) stream.
    /// </summary>
    public event Action<byte[], uint, bool, string?>? FrameReceived;

    /// <summary>
    /// Raised when the peer requests a key frame via an inbound PLI/FIR (RFC 4585/5104); the app should
    /// encode and send a key frame.
    /// </summary>
    public event Action? KeyFrameRequested;

    /// <summary>Total reassembled inbound frames delivered.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Total inbound key frames delivered.</summary>
    public long KeyFrames => Interlocked.Read(ref _keyFrames);

    /// <summary>
    /// Frames discarded because a reorder gap the window could not fill tore the frame under assembly, so the
    /// depacketiser was reset before feeding on. The receiver-side "frames dropped" metric for this transport-only
    /// path (a partially-assembled frame is never emitted after a discontinuity).
    /// </summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Generic NACK feedback messages this stream has sent to the peer on detected inbound loss.</summary>
    public long NacksSent => _keyFrameFeedback.NacksSent;

    /// <summary>PLI keyframe requests this stream has sent to the peer on detected inbound loss (throttled).</summary>
    public long PlisSent => _keyFrameFeedback.PlisSent;

    /// <summary>Whether this track sends multiple simulcast encodings (RFC 8853).</summary>
    public bool IsSimulcast => _layers.Count > 0;

    /// <summary>The configured simulcast <c>a=rid</c> layer ids (empty for a non-simulcast track).</summary>
    public IReadOnlyCollection<string> SendRids => _layers.Keys.ToArray();

    /// <summary>Builds a non-simulcast video track (one RTP stream on the video MID).</summary>
    /// <param name="mid">The video m-line's MID token.</param>
    /// <param name="codecName">The negotiated video codec ("H264"/"VP8").</param>
    /// <param name="payloadType">The negotiated RTP payload type.</param>
    /// <param name="localSsrc">This stream's outbound SSRC — the SenderSsrc of any outbound NACK/PLI.</param>
    /// <param name="remoteSupportsNack">Whether the peer advertised Generic NACK (RFC 4585) for this m-line.</param>
    /// <param name="remoteSupportsPli">Whether the peer advertised PLI (RFC 4585 §6.3.1) for this m-line.</param>
    /// <param name="outbound">The bundle's outbound pipeline (RTP sends and the SRTCP-protected RTCP send path).</param>
    /// <param name="reorderWindowDepth">The inbound reorder window depth in packets.</param>
    /// <param name="loggerFactory">Builds the loggers for the track and its feedback path.</param>
    /// <param name="rtxPayloadType">
    /// The negotiated RTX repair payload type (RFC 4588), or <see langword="null"/> when RTX was not
    /// negotiated. When present the track retains its sent packets and answers an inbound Generic NACK by
    /// resending them on a separate RTX stream (own SSRC, this payload type, OSN-prefixed).
    /// </param>
    /// <param name="rtxSsrc">
    /// The RTX repair stream's SSRC, allocated bundle-wide-distinct by the session factory (RFC 4588 §4 /
    /// RFC 3550 §8.1). When <see langword="null"/> the track picks a repair SSRC distinct from
    /// <paramref name="localSsrc"/> only — the fallback for callers that do not own the bundle SSRC set.
    /// </param>
    /// <param name="receiveRids">The <c>a=rid</c> allowlist for inbound demultiplexing, or null/empty for none.</param>
    /// <param name="opaqueFrames">
    /// True when this track's frames are end-to-end encrypted and must not be interpreted (#223, ADR-068): the
    /// track then resolves the opaque payload-format pair, which works from the RTP framing alone and makes no
    /// key-frame claim. Default <see langword="false"/> keeps the clear-media pair and its key-frame detection.
    /// </param>
    public BundledVideoTrack(
        string mid,
        string codecName,
        byte payloadType,
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
        BundledOutboundPipeline outbound,
        int reorderWindowDepth,
        ILoggerFactory loggerFactory,
        byte? rtxPayloadType = null,
        uint? rtxSsrc = null,
        IReadOnlyList<string>? receiveRids = null,
        bool opaqueFrames = false,
        byte? dependencyDescriptorExtensionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _dependencyDescriptorId = dependencyDescriptorExtensionId;
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = loggerFactory.CreateLogger<BundledVideoTrack>();
        _codecName = codecName;
        _reorderWindowDepth = reorderWindowDepth;
        _localSsrc = localSsrc;
        _receiveRids = ToRidSet(receiveRids);
        _opaqueFrames = opaqueFrames;

        var (packetiser, depacketiser) = CreatePayloadFormat(codecName, opaqueFrames);
        _defaultLane = new BundledVideoInboundLayer(rid: null, depacketiser, new VideoReorderBuffer(reorderWindowDepth));
        _single = new BundledVideoSendEncoding(rid: null, payloadType, packetiser);
        _layers = new Dictionary<string, BundledVideoSendEncoding>(StringComparer.Ordinal);
        _videoPayloadType = payloadType;

        // RTX repair stream (RFC 4588): retain sent packets so an inbound NACK can be answered by resending
        // them on a repair SSRC. The factory allocates that SSRC distinct from every outbound SSRC on the
        // bundle (RFC 3550 §8.1); absent one, fall back to a full-range SSRC distinct from this stream's.
        // The retransmit callback below then resends; without RTX it stays a no-op (feedback built plain).
        if (rtxPayloadType is { } rtxPt)
        {
            _rtxPayloadType = rtxPt;
            _rtxConfigured = true;
            _rtxSsrc = rtxSsrc ?? RtpRandom.NextSsrc(distinctFrom: localSsrc);
            _retransmitBuffer = new RtpRetransmissionBuffer();
            // Retain only THIS stream's sent packets, keyed by sequence number. The pipeline's PacketSent fires
            // for every SSRC on the shared bundle (audio, other video), but an inbound NACK names this stream's
            // SSRC sequence space — retaining another SSRC's packets under the same sequence key would let a NACK
            // resend an unrelated (e.g. audio) packet as this stream's RTX. Filter to localSsrc.
            _retainSent = packet =>
            {
                if (packet.Ssrc == localSsrc)
                    _retransmitBuffer.Store(packet);
            };
            _outbound.PacketSent += _retainSent;
            _keyFrameFeedback = BuildFeedback(
                localSsrc, remoteSupportsNack, remoteSupportsPli, loggerFactory, OnRetransmitRequested);
        }
        else
        {
            _keyFrameFeedback = BuildFeedback(localSsrc, remoteSupportsNack, remoteSupportsPli, loggerFactory);
        }
    }

    /// <summary>
    /// Builds a simulcast video track (RFC 8853): one RTP stream per <paramref name="rids"/> layer under
    /// the shared MID, each with its own packetiser and send lock. On receive, each inbound RID is
    /// demultiplexed into its own reassembly lane (built lazily on first sighting).
    /// </summary>
    /// <param name="mid">The video m-line's MID token.</param>
    /// <param name="codecName">The negotiated video codec ("H264"/"VP8").</param>
    /// <param name="payloadType">The negotiated RTP payload type shared by every layer.</param>
    /// <param name="localSsrc">
    /// The primary outbound SSRC — the SenderSsrc of any outbound NACK/PLI. The RTCP loss/keyframe feedback
    /// is shared across the receive lanes; it names loss by the arriving packet's SSRC, which is already
    /// per-encoding correct.
    /// </param>
    /// <param name="remoteSupportsNack">Whether the peer advertised Generic NACK (RFC 4585) for this m-line.</param>
    /// <param name="remoteSupportsPli">Whether the peer advertised PLI (RFC 4585 §6.3.1) for this m-line.</param>
    /// <param name="rids">The <c>a=rid</c> layer ids to send under the shared MID.</param>
    /// <param name="outbound">The bundle's outbound pipeline (RTP sends and the SRTCP-protected RTCP send path).</param>
    /// <param name="reorderWindowDepth">The inbound reorder window depth in packets.</param>
    /// <param name="loggerFactory">Builds the loggers for the track and its feedback path.</param>
    /// <param name="receiveRids">The <c>a=rid</c> allowlist for inbound demultiplexing, or null/empty for none.</param>
    /// <param name="opaqueFrames">
    /// True when this track's frames are end-to-end encrypted and must not be interpreted (#223, ADR-068). Every
    /// send layer and every receive lane — including a lane built lazily on first RID sighting — then resolves the
    /// opaque payload-format pair. Default <see langword="false"/> keeps the clear-media pair.
    /// </param>
    public BundledVideoTrack(
        string mid,
        string codecName,
        byte payloadType,
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
        IReadOnlyList<string> rids,
        BundledOutboundPipeline outbound,
        int reorderWindowDepth,
        ILoggerFactory loggerFactory,
        IReadOnlyList<string>? receiveRids = null,
        bool opaqueFrames = false,
        byte? dependencyDescriptorExtensionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(rids);
        _dependencyDescriptorId = dependencyDescriptorExtensionId;
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (rids.Count == 0)
            throw new ArgumentException("A simulcast video track needs at least one rid.", nameof(rids));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = loggerFactory.CreateLogger<BundledVideoTrack>();
        _codecName = codecName;
        _reorderWindowDepth = reorderWindowDepth;
        _localSsrc = localSsrc;
        _receiveRids = ToRidSet(receiveRids);
        _opaqueFrames = opaqueFrames;

        // The default receive lane handles the base/RID-less inbound stream; a per-RID lane is built lazily
        // on first RID sighting (recv-side simulcast demux, RFC 8853). Each send layer gets its own packetiser
        // (the packetiser is stateful, so layers must not share one).
        _defaultLane = new BundledVideoInboundLayer(
            rid: null, CreatePayloadFormat(codecName, opaqueFrames).Depacketiser, new VideoReorderBuffer(reorderWindowDepth));

        var layers = new Dictionary<string, BundledVideoSendEncoding>(rids.Count, StringComparer.Ordinal);
        foreach (var rid in rids)
        {
            ArgumentException.ThrowIfNullOrEmpty(rid);
            if (!layers.TryAdd(rid, new BundledVideoSendEncoding(rid, payloadType, CreatePayloadFormat(codecName, opaqueFrames).Packetiser)))
                throw new ArgumentException($"Duplicate simulcast rid '{rid}'.", nameof(rids));
        }
        _layers = layers;
        _keyFrameFeedback = BuildFeedback(localSsrc, remoteSupportsNack, remoteSupportsPli, loggerFactory);
    }

    // Builds the RFC 4585/5104 feedback for this stream over the bundle's SRTCP send path, mirroring the
    // single-stream VideoRtpStream construction. Inbound NACK is routed to onRetransmitRequested — the RTX
    // resend hook (RFC 4588) when RTX is negotiated, or a no-op (the default) when it is not or on simulcast.
    private VideoKeyFrameFeedback BuildFeedback(
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
        ILoggerFactory loggerFactory,
        Action<IReadOnlyList<ushort>>? onRetransmitRequested = null) =>
        new(
            new RtcpPacketCodec(),
            localSsrc,
            remoteSupportsNack,
            remoteSupportsPli,
            // Key-frame feedback keeps no reporting state, so the send outcome (#162 P2-5) is not consulted:
            // a suppressed PLI/NACK is re-driven by the next loss or the next key-frame need.
            async (datagram, ct) => await _outbound.SendRtcpAsync(datagram, ct).ConfigureAwait(false),
            () => KeyFrameRequested?.Invoke(),
            onRetransmitRequested ?? (_ => { }),
            loggerFactory.CreateLogger<VideoKeyFrameFeedback>(),
            _lifetimeCts.Token);

    // Answers an inbound Generic NACK (RFC 4585) by resending the requested packets on the RTX repair stream
    // (RFC 4588 §4): each still in the retention buffer is re-wrapped with the rtx payload type, the repair
    // SSRC, and a fresh monotonically increasing rtx sequence number, then sent over the shared transport. A
    // packet no longer in the window is simply not resent. Runs on the bundle receive loop (VideoKeyFrameFeedback
    // dispatches from OnRtcpPackets); the resend is fire-and-forget so it never blocks that loop.
    private void OnRetransmitRequested(IReadOnlyList<ushort> sequenceNumbers)
    {
        if (_retransmitBuffer is null)
            return;

        foreach (var seq in sequenceNumbers)
        {
            if (!_retransmitBuffer.TryGet(seq, out var original))
                continue;

            var rtxSeq = unchecked((ushort)Interlocked.Increment(ref _rtxSequence));
            var rtx = RtxPacketFactory.Encapsulate(original, _rtxPayloadType, _rtxSsrc, rtxSeq);
            _ = SendRtxAsync(rtx);
        }
    }

    private async Task SendRtxAsync(RtpPacket rtx)
    {
        try
        {
            await _outbound.SendRtxAsync(rtx, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Teardown while retransmitting — nothing to recover.
            _logger.LogTrace("Bundled RTX retransmission aborted by session teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send a bundled RTX retransmission.");
        }
    }

    /// <summary>
    /// Packetises one encoded frame and sends its payloads over the shared transport on the video MID.
    /// All payloads share <paramref name="rtpTimestamp"/> and are sent atomically (RFC 6184 §5.1 /
    /// RFC 7741 §4.1: the marker bit closes the frame on the last payload).
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a simulcast track — send with a rid instead.</exception>
    public Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default)
    {
        if (_single is not { } single)
            throw new InvalidOperationException("This is a simulcast video track; send with a rid via SendFrameAsync(rid, …).");
        return SendOnEncodingAsync(single, encodedFrame, rtpTimestamp, ct);
    }

    /// <summary>
    /// Packetises one encoded frame and sends it on the given simulcast <paramref name="rid"/> layer's RTP
    /// stream (RFC 8853), stamping the RID per packet. Layers send independently.
    /// </summary>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rid);
        if (!_layers.TryGetValue(rid, out var encoding))
            throw new ArgumentException($"No simulcast encoding is configured for rid '{rid}'.", nameof(rid));
        return SendOnEncodingAsync(encoding, encodedFrame, rtpTimestamp, ct);
    }

    private async Task SendOnEncodingAsync(BundledVideoSendEncoding encoding, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct)
    {
        var payloads = encoding.Packetiser.Packetise(encodedFrame, MaxRtpPayloadSize);

        // Serialize whole frames per encoding: interleaving two frames' packets would corrupt the peer's
        // reassembly of that RTP stream.
        await encoding.SendSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var payload in payloads)
                await _outbound.SendTimestampedAsync(
                        _mid, payload.Payload, payload.IsLastOfFrame, encoding.PayloadType, rtpTimestamp, encoding.Rid, ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            encoding.SendSync.Release();
        }
    }

    /// <summary>
    /// The router sink for the video MID: reorders an arriving RTP packet and depacketises released
    /// packets into frames. On a simulcast receive the packet's resolved <c>a=rid</c> (RFC 8853/8852)
    /// selects a per-encoding reassembly lane so interleaved encodings never corrupt each other's reorder
    /// state or raise phantom NACKs; <see langword="null"/> (non-simulcast, or the base RID-less stream)
    /// uses the default lane. When RTX was negotiated the peer's repair stream shares this MID (its rtx SSRC
    /// carries the same <c>a=mid</c>, RFC 9143), so a packet on the rtx payload type is decapsulated (RFC 4588 §4)
    /// and the recovered original is fed into the default lane — filling the gap that prompted the NACK
    /// (RTX is non-simulcast-only). Runs on the bundle receive loop (single consumer).
    /// </summary>
    /// <param name="packet">The inbound RTP packet.</param>
    /// <param name="rid">
    /// The packet's resolved simulcast RID (RFC 8852), or <see langword="null"/> for the default stream.
    /// </param>
    public void OnRtpPacket(RtpPacket packet, string? rid = null)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Inbound RTX recovery (RFC 4588 §4): a repair packet is not a new primary arrival — it must not drive
        // arrival-order loss detection (that would NACK the very gaps a retransmit is closing, a NACK storm) nor
        // be inserted raw. Strip the OSN prefix to recover the original, then feed it through the default lane's
        // reorder path exactly like a primary packet. RTX is non-simulcast-only, so it never targets a RID lane.
        // Mirrors VideoRtpStream's separate secondary receive path.
        if (_rtxConfigured && packet.PayloadType == _rtxPayloadType)
        {
            if (!RtxPacketFactory.TryDecapsulate(packet, _videoPayloadType, _remoteMediaSsrc, out var original))
            {
                _logger.LogDebug("Dropping bundled RTX packet too short to carry an original sequence number.");
                return;
            }

            Enqueue(_defaultLane, original!);
            return;
        }

        var lane = LayerFor(rid);
        if (lane is null)
            return;

        // Primary arrival: remember the remote media SSRC so a recovered RTX packet can be stamped with it.
        // Single field track-wide — RTX is non-simulcast, so on a simulcast receive this is overwritten by the
        // last encoding seen; per-layer PLI/RTX is follow-up work and needs a per-lane media SSRC.
        _remoteMediaSsrc = packet.Ssrc;

        // Arrival-order loss signalling (RFC 4585), per lane: the tracker holds a forward gap for a small
        // reorder window and only reports it once it ages past that window. Per lane is essential — one shared
        // tracker fed the interleaved sequence spaces of several SSRCs would see phantom gaps and NACK-storm.
        // A reordered packet that arrives first is never NACKed (Track returns null; the reorder buffer below
        // corrects it). The feedback names loss by packet.Ssrc, which is already per-encoding. Mirrors VideoRtpStream.
        if (lane.ArrivalLoss.Track(packet.SequenceNumber) is { } missing)
            _keyFrameFeedback.OnLoss(packet.Ssrc, missing);

        Enqueue(lane, packet);
    }

    // Resolves the reassembly lane for a resolved RID: null → the default lane; a RID → its lane, built lazily
    // on first sighting (a browser stamps the RID extension only on the first packets of each encoding). Safe
    // as a plain dictionary because OnRtpPacket is single-consumer (see the class remarks).
    // DoS cap on distinct inbound RID lanes (ENGINEERING_RULES.md §132-133). With the RID extension negotiated
    // an authenticated peer can stamp a fresh RID on every packet; each new RID here allocates a depacketiser
    // and a reorder buffer, so an unbounded lane map exhausts process memory. Legitimate simulcast uses a
    // handful of encodings, so cap the lanes generously and drop packets for further RIDs.
    private const int MaxInboundRidLanes = 8;

    // Resolves the reassembly lane for a resolved RID, or null when the RID is new and the lane cap is reached
    // (caller drops the packet). A known RID or the default (null) lane always resolves.
    private BundledVideoInboundLayer? LayerFor(string? rid)
    {
        if (rid is null)
            return _defaultLane;
        if (_ridLayers.TryGetValue(rid, out var lane))
            return lane;

        // A RID outside the negotiated receive set never gets a lane (#161 P3-15). The cap below bounds how
        // much an unknown RID can cost; this decides whether it is entitled to anything at all. With no
        // receive simulcast negotiated the set is empty and every RID is admitted, as before.
        if (_receiveRids.Count > 0 && !_receiveRids.Contains(rid))
        {
            _logger.LogDebug(
                "Dropping inbound video packet for rid '{Rid}' on MID {Mid}: not among the negotiated receive " +
                "RIDs (RFC 8853).", rid, _mid);
            return null;
        }

        if (_ridLayers.Count >= MaxInboundRidLanes)
        {
            _logger.LogWarning(
                "Bundled video RID lane cap {Cap} reached; dropping packet for rid '{Rid}' (RFC 8853 recv-side simulcast).",
                MaxInboundRidLanes, rid);
            return null;
        }
        lane = new BundledVideoInboundLayer(
            rid, CreatePayloadFormat(_codecName, _opaqueFrames).Depacketiser, new VideoReorderBuffer(_reorderWindowDepth));
        _ridLayers[rid] = lane;
        return lane;
    }

    // The payload-format pair for this track: the clear-media one (which reads the frame to detect key frames)
    // or the opaque one for end-to-end encrypted frames, which works from the RTP framing alone (#223, ADR-068).
    // One place, so every send layer and receive lane of a track resolves the same policy.
    private static (IVideoPacketiser Packetiser, IVideoDepacketiser Depacketiser) CreatePayloadFormat(
        string codecName, bool opaqueFrames) =>
        opaqueFrames ? VideoPayloadFormat.CreateOpaque(codecName) : VideoPayloadFormat.Create(codecName);

    // Feeds one video packet (freshly received or RTX-recovered) through the given lane's reorder window toward
    // its depacketiser. The window releases in ascending sequence order (letting a late retransmit slot into its
    // gap) and drops duplicates and too-late sequences — so an RTX for a sequence that was never missing, or
    // already released, is harmlessly absorbed. Mirrors VideoRtpStream.Enqueue.
    private void Enqueue(BundledVideoInboundLayer lane, RtpPacket packet)
    {
        foreach (var released in lane.ReorderBuffer.Insert(packet))
            DeliverOrdered(lane, released);
    }

    /// <summary>
    /// Handles the decoded inbound RTCP compound (already SRTCP-unprotected and parsed once by the session):
    /// a PLI or FIR naming one of this track's sending SSRCs (RFC 4585/5104) is treated as a request to send a
    /// key frame on this stream (surfaced on <see cref="KeyFrameRequested"/>); an inbound Generic NACK naming
    /// one is routed to the RTX retransmit path (RFC 4588 — resent on this track's repair stream when RTX was
    /// negotiated, a no-op otherwise). Runs on the bundle receive loop.
    /// <para>
    /// The compound is filtered to this track first (#161 P2-5). On a BUNDLE the whole compound reaches every
    /// track over the single shared RTCP channel, and the feedback handler it delegates to deliberately
    /// ignores the media SSRC — correct for the dedicated single-stream video channel it also serves, wrong
    /// here: a PLI for one m-line would ask every video track for a key frame, and a NACK for one would look
    /// up its sequence numbers in another track's retransmission buffer, whose 16-bit sequence space overlaps,
    /// resending unrelated packets as this stream's RTX.
    /// </para>
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        var mine = FilterToThisTrack(packets);
        if (mine.Count > 0)
            _keyFrameFeedback.OnRtcpPackets(mine);
    }

    // Keeps the feedback messages that name one of this track's sending SSRCs and drops the rest. Everything
    // else in the compound (SR/RR/BYE/transport-cc) is consumed by the dispatcher, never by this path, so it
    // is left out too. Feedback naming an SSRC we do not send — including a lenient peer's 0 — is dropped:
    // on a shared channel it cannot be attributed, and acting on it is what the finding describes.
    private IReadOnlyList<RtcpPacket> FilterToThisTrack(IReadOnlyList<RtcpPacket> packets)
    {
        List<RtcpPacket>? mine = null;
        foreach (var packet in packets)
        {
            bool ours;
            switch (packet)
            {
                case RtcpPictureLossIndication pli:
                    ours = OwnsMediaSsrc(pli.MediaSsrc);
                    break;
                case RtcpGenericNack nack:
                    ours = OwnsMediaSsrc(nack.MediaSsrc);
                    break;
                // FIR names its targets in the FCI entries, not in a header field (RFC 5104 §4.3.1).
                case RtcpFullIntraRequest fir:
                    ours = fir.Entries.Any(entry => OwnsMediaSsrc(entry.MediaSsrc));
                    break;
                default:
                    continue;
            }

            if (!ours)
            {
                _logger.LogTrace(
                    "Dropping inbound RTCP feedback on MID {Mid}: it names a media SSRC this track does not send.",
                    _mid);
                continue;
            }

            (mine ??= new List<RtcpPacket>(packets.Count)).Add(packet);
        }

        return mine ?? (IReadOnlyList<RtcpPacket>)Array.Empty<RtcpPacket>();
    }

    private static IReadOnlySet<string> ToRidSet(IReadOnlyList<string>? rids)
        => rids is null || rids.Count == 0
            ? EmptyRidSet
            : new HashSet<string>(rids, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> EmptyRidSet = new HashSet<string>(StringComparer.Ordinal);

    // This track's sending SSRCs: the primary stream, plus every simulcast layer registered under this MID
    // (their SSRCs live on the outbound pipeline, not on the track). The RTX repair SSRC is deliberately not
    // included — a NACK naming the repair stream would otherwise trigger a retransmit of a retransmit.
    private bool OwnsMediaSsrc(uint ssrc) => ssrc == _localSsrc || _outbound.OwnsSsrc(_mid, ssrc);

    /// <summary>
    /// Asks the peer for a fresh key frame on the app's demand (RFC 4585 §6.3.1) by sending a PLI naming the
    /// received stream's media SSRC — for the receiving side when a new renderer or a decoder reset needs an
    /// intra frame, independent of detected loss. A no-op returning <see langword="false"/> when the peer did
    /// not advertise PLI or the shared 500 ms throttle still holds. Thread-safe; shares the loss path's throttle.
    /// </summary>
    public ValueTask<bool> RequestKeyFrameAsync(CancellationToken cancellationToken = default)
        => _keyFrameFeedback.RequestKeyFrameAsync(_remoteMediaSsrc, cancellationToken);

    // Delivers one packet in sequence order to the lane's depacketiser. A discontinuity is a gap the reorder
    // window could not fill: the frame under assembly is torn, so reset before feeding on. Ordered-delivery
    // state (HasDelivered/LastDeliveredSequence) is per lane so one encoding's gap never resets another's.
    private void DeliverOrdered(BundledVideoInboundLayer lane, RtpPacket packet)
    {
        if (lane.HasDelivered && packet.SequenceNumber != unchecked((ushort)(lane.LastDeliveredSequence + 1)))
        {
            lane.Depacketiser.Reset();
            Interlocked.Increment(ref _framesDropped);
        }
        lane.LastDeliveredSequence = packet.SequenceNumber;
        lane.HasDelivered = true;

        // Dependency Descriptor (#225), when the peer negotiated it: the key frame and the layer come from
        // the RTP header rather than the payload. Read before reassembly because the descriptor rides on
        // each packet while the facts belong to the frame — the one starting the frame is the one that
        // describes it.
        var descriptor = ReadDescriptor(lane, packet);
        if (descriptor is not null && (descriptor.StartOfFrame || lane.PendingDescriptor is null))
            lane.PendingDescriptor = descriptor;

        if (!lane.Depacketiser.TryProcess(packet.Payload, packet.Timestamp, packet.Marker, out var frame, out var isKeyFrame))
            return;

        // The descriptor wins where it exists: for an end-to-end encrypted stream the payload-derived flag is
        // a guess about ciphertext (#223), and even in the clear the sender knows better than the parser does.
        var frameDescriptor = lane.PendingDescriptor;
        lane.PendingDescriptor = null;
        if (frameDescriptor is not null)
            isKeyFrame = frameDescriptor.IsKeyFrame;

        Interlocked.Increment(ref _framesReceived);
        if (isKeyFrame)
        {
            Interlocked.Increment(ref _keyFrames);
        }

        try
        {
            FrameReceived?.Invoke(frame!, packet.Timestamp, isKeyFrame, lane.Rid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled video FrameReceived handler.");
        }
    }

    // Parses the Dependency Descriptor from a packet's header extension, in whichever RFC 8285 wire form it
    // arrived (#224 — the descriptor is what needed the two-byte one). Null when the extension was not
    // negotiated, is absent from this packet, or is malformed; the caller then keeps the payload-derived flag.
    private DependencyDescriptor? ReadDescriptor(BundledVideoInboundLayer lane, RtpPacket packet)
    {
        if (_dependencyDescriptorId is not { } id)
            return null;
        if (!RtpHeaderExtensions.TryFindValue(packet.HeaderExtension, id, out var value))
            return null;

        // The reader is the lane's: each simulcast encoding is its own stream with its own template structure.
        return lane.Descriptors.TryParse(value, out var descriptor) ? descriptor : null;
    }

    /// <summary>
    /// Releases the per-encoding send locks. Like the single-stream video path, this must not race an
    /// in-flight <see cref="SendFrameAsync(System.ReadOnlyMemory{byte}, uint, System.Threading.CancellationToken)"/>:
    /// the owning peer drains in-flight sends before tearing the session down (WebRtcPeerConnection's send
    /// gate, HARD-C6), so a send never observes a disposed semaphore.
    /// </summary>
    public void Dispose()
    {
        // Cancel any in-flight NACK/PLI/RTX send before releasing the send locks, so a feedback or retransmit
        // send never races teardown (the SRTP/SRTCP send path also fails closed on a disposed context).
        _lifetimeCts.Cancel();
        // Stop retaining sent packets before the pipeline it subscribes to goes away.
        if (_retainSent is not null)
            _outbound.PacketSent -= _retainSent;
        FrameReceived = null;
        KeyFrameRequested = null;
        _single?.Dispose();
        foreach (var layer in _layers.Values)
            layer.Dispose();
        _lifetimeCts.Dispose();
    }
}
