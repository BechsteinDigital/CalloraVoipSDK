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
/// The receive path (<see cref="OnRtpPacket"/>) is single-consumer — the depacketiser is stateful and not
/// thread-safe, so it must be driven only from the bundle's single receive loop, exactly as the
/// single-stream video path drives it from the RTP receive loop. Sends are serialised per encoding so a
/// frame's packets never interleave with another frame's on the same RTP stream; distinct simulcast
/// encodings (distinct SSRCs) send independently.
/// <para>
/// Send-side simulcast (RFC 8853): when built with encodings, the track sends N independent RTP streams
/// under one MID — one per <c>a=rid</c> layer, each on its own SSRC with the RID stamped per packet
/// (RFC 8852). The receive path stays single-stream; receive-side RID demux is out of scope.
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
    private readonly IVideoDepacketiser _depacketiser;
    private readonly VideoReorderBuffer _reorderBuffer;
    private readonly ILogger<BundledVideoTrack> _logger;

    // RTCP keyframe/loss feedback for this stream (RFC 4585/5104), mirroring the single-stream
    // VideoRtpStream: inbound PLI/FIR → KeyFrameRequested; detected inbound loss → outbound NACK/PLI.
    private readonly VideoKeyFrameFeedback _keyFrameFeedback;
    private readonly VideoArrivalLossTracker _arrivalLoss = new();
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

    // Receive-loop-only ordered-delivery state (reset the depacketiser on a genuine gap so a fragment of
    // a lost packet is never glued to the next frame).
    private bool _hasDelivered;
    private ushort _lastDeliveredSequence;
    private long _framesReceived;
    private long _keyFrames;
    private long _framesDropped;

    /// <summary>Raised with a reassembled encoded frame, its RTP timestamp, and whether it is a key frame.</summary>
    public event Action<byte[], uint, bool>? FrameReceived;

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
        uint? rtxSsrc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = loggerFactory.CreateLogger<BundledVideoTrack>();

        var (packetiser, depacketiser) = VideoPayloadFormat.Create(codecName);
        _depacketiser = depacketiser;
        _reorderBuffer = new VideoReorderBuffer(reorderWindowDepth);
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
    /// the shared MID, each with its own packetiser and send lock. The receive path stays single-stream.
    /// </summary>
    /// <param name="mid">The video m-line's MID token.</param>
    /// <param name="codecName">The negotiated video codec ("H264"/"VP8").</param>
    /// <param name="payloadType">The negotiated RTP payload type shared by every layer.</param>
    /// <param name="localSsrc">
    /// The primary outbound SSRC — the SenderSsrc of any outbound NACK/PLI. The single-stream receive path
    /// (and thus loss feedback) is shared across the simulcast layers, matching the single-stream receive scope.
    /// </param>
    /// <param name="remoteSupportsNack">Whether the peer advertised Generic NACK (RFC 4585) for this m-line.</param>
    /// <param name="remoteSupportsPli">Whether the peer advertised PLI (RFC 4585 §6.3.1) for this m-line.</param>
    /// <param name="rids">The <c>a=rid</c> layer ids to send under the shared MID.</param>
    /// <param name="outbound">The bundle's outbound pipeline (RTP sends and the SRTCP-protected RTCP send path).</param>
    /// <param name="reorderWindowDepth">The inbound reorder window depth in packets.</param>
    /// <param name="loggerFactory">Builds the loggers for the track and its feedback path.</param>
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
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(rids);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (rids.Count == 0)
            throw new ArgumentException("A simulcast video track needs at least one rid.", nameof(rids));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = loggerFactory.CreateLogger<BundledVideoTrack>();

        // One depacketiser drives the single-stream receive path; each send layer gets its own packetiser
        // (the packetiser is stateful, so layers must not share one).
        _depacketiser = VideoPayloadFormat.Create(codecName).Depacketiser;
        _reorderBuffer = new VideoReorderBuffer(reorderWindowDepth);

        var layers = new Dictionary<string, BundledVideoSendEncoding>(rids.Count, StringComparer.Ordinal);
        foreach (var rid in rids)
        {
            ArgumentException.ThrowIfNullOrEmpty(rid);
            if (!layers.TryAdd(rid, new BundledVideoSendEncoding(rid, payloadType, VideoPayloadFormat.Create(codecName).Packetiser)))
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
            _outbound.SendRtcpAsync,
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
    /// packets into frames. When RTX was negotiated the peer's repair stream shares this MID (its rtx SSRC
    /// carries the same <c>a=mid</c>, RFC 9143), so a packet on the rtx payload type is decapsulated (RFC 4588 §4)
    /// and the recovered original is fed into the same reorder window — filling the gap that prompted the NACK.
    /// Runs on the bundle receive loop (single consumer).
    /// </summary>
    public void OnRtpPacket(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Inbound RTX recovery (RFC 4588 §4): a repair packet is not a new primary arrival — it must not drive
        // arrival-order loss detection (that would NACK the very gaps a retransmit is closing, a NACK storm) nor
        // be inserted raw. Strip the OSN prefix to recover the original, then feed it through the shared reorder
        // path exactly like a primary packet. Mirrors VideoRtpStream's separate secondary receive path.
        if (_rtxConfigured && packet.PayloadType == _rtxPayloadType)
        {
            if (!RtxPacketFactory.TryDecapsulate(packet, _videoPayloadType, _remoteMediaSsrc, out var original))
            {
                _logger.LogDebug("Dropping bundled RTX packet too short to carry an original sequence number.");
                return;
            }

            Enqueue(original!);
            return;
        }

        // Primary arrival: remember the remote media SSRC so a recovered RTX packet can be stamped with it.
        _remoteMediaSsrc = packet.Ssrc;

        // Arrival-order loss signalling (RFC 4585): the tracker holds a forward gap for a small reorder window
        // and only reports it once it ages past that window — a reordered packet that arrives first is never
        // NACKed (Track returns null for a reorder/duplicate; the reorder buffer below corrects it). Mirrors
        // VideoRtpStream.
        if (_arrivalLoss.Track(packet.SequenceNumber) is { } missing)
            _keyFrameFeedback.OnLoss(packet.Ssrc, missing);

        Enqueue(packet);
    }

    // Feeds one video packet (freshly received or RTX-recovered) through the reorder window toward the
    // depacketiser. The window releases in ascending sequence order (letting a late retransmit slot into its
    // gap) and drops duplicates and too-late sequences — so an RTX for a sequence that was never missing, or
    // already released, is harmlessly absorbed. Mirrors VideoRtpStream.Enqueue.
    private void Enqueue(RtpPacket packet)
    {
        foreach (var released in _reorderBuffer.Insert(packet))
            DeliverOrdered(released);
    }

    /// <summary>
    /// Handles the decoded inbound RTCP compound (already SRTCP-unprotected and parsed once by the session):
    /// a PLI or FIR anywhere in it (RFC 4585/5104) is treated as a request to send a key frame on this stream
    /// (surfaced on <see cref="KeyFrameRequested"/>); an inbound Generic NACK is routed to the RTX retransmit
    /// path (RFC 4588 — resent on this track's repair stream when RTX was negotiated, a no-op otherwise).
    /// Delegates to the shared <see cref="VideoKeyFrameFeedback"/>, mirroring the single-stream video path.
    /// Runs on the bundle receive loop.
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        _keyFrameFeedback.OnRtcpPackets(packets);
    }

    /// <summary>
    /// Asks the peer for a fresh key frame on the app's demand (RFC 4585 §6.3.1) by sending a PLI naming the
    /// received stream's media SSRC — for the receiving side when a new renderer or a decoder reset needs an
    /// intra frame, independent of detected loss. A no-op returning <see langword="false"/> when the peer did
    /// not advertise PLI or the shared 500 ms throttle still holds. Thread-safe; shares the loss path's throttle.
    /// </summary>
    public ValueTask<bool> RequestKeyFrameAsync(CancellationToken cancellationToken = default)
        => _keyFrameFeedback.RequestKeyFrameAsync(_remoteMediaSsrc, cancellationToken);

    // Delivers one packet in sequence order to the depacketiser. A discontinuity is a gap the reorder
    // window could not fill: the frame under assembly is torn, so reset before feeding on.
    private void DeliverOrdered(RtpPacket packet)
    {
        if (_hasDelivered && packet.SequenceNumber != unchecked((ushort)(_lastDeliveredSequence + 1)))
        {
            _depacketiser.Reset();
            Interlocked.Increment(ref _framesDropped);
        }
        _lastDeliveredSequence = packet.SequenceNumber;
        _hasDelivered = true;

        if (!_depacketiser.TryProcess(packet.Payload, packet.Timestamp, packet.Marker, out var frame, out var isKeyFrame))
            return;

        Interlocked.Increment(ref _framesReceived);
        if (isKeyFrame)
        {
            Interlocked.Increment(ref _keyFrames);
        }

        try
        {
            FrameReceived?.Invoke(frame!, packet.Timestamp, isKeyFrame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled video FrameReceived handler.");
        }
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
