using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
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

    // RTP payload budget: MTU minus RTP/SRTP/extension overhead (mirrors the single-stream video path).
    private const int MaxRtpPayloadSize = 1200;

    // Receive-loop-only ordered-delivery state (reset the depacketiser on a genuine gap so a fragment of
    // a lost packet is never glued to the next frame).
    private bool _hasDelivered;
    private ushort _lastDeliveredSequence;
    private long _framesReceived;
    private long _keyFrames;

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
    public BundledVideoTrack(
        string mid,
        string codecName,
        byte payloadType,
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
        BundledOutboundPipeline outbound,
        int reorderWindowDepth,
        ILoggerFactory loggerFactory)
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
        _keyFrameFeedback = BuildFeedback(localSsrc, remoteSupportsNack, remoteSupportsPli, loggerFactory);
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
    // single-stream VideoRtpStream construction. RTX is out of scope for this slice, so the retransmit
    // callback (inbound NACK) is a no-op; it becomes the RTX resend hook in a later slice.
    private VideoKeyFrameFeedback BuildFeedback(
        uint localSsrc, bool remoteSupportsNack, bool remoteSupportsPli, ILoggerFactory loggerFactory) =>
        new(
            new RtcpPacketCodec(),
            localSsrc,
            remoteSupportsNack,
            remoteSupportsPli,
            _outbound.SendRtcpAsync,
            () => KeyFrameRequested?.Invoke(),
            _ => { },
            loggerFactory.CreateLogger<VideoKeyFrameFeedback>(),
            _lifetimeCts.Token);

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
    /// packets into frames. Runs on the bundle receive loop (single consumer).
    /// </summary>
    public void OnRtpPacket(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Arrival-order loss signalling (RFC 4585), on the raw arrival sequence — before the reorder window
        // can slide past a genuine forward gap. A reorder or duplicate is not loss (Track returns null): the
        // reorder buffer below corrects it, so it raises neither a NACK nor a PLI. Mirrors VideoRtpStream.
        if (_arrivalLoss.Track(packet.SequenceNumber) is { } missing)
            _keyFrameFeedback.OnLoss(packet.Ssrc, missing);

        foreach (var released in _reorderBuffer.Insert(packet))
            DeliverOrdered(released);
    }

    /// <summary>
    /// Handles the decoded inbound RTCP compound (already SRTCP-unprotected and parsed once by the session):
    /// a PLI or FIR anywhere in it (RFC 4585/5104) is treated as a request to send a key frame on this stream
    /// (surfaced on <see cref="KeyFrameRequested"/>); an inbound Generic NACK is routed to the retransmit path
    /// (a no-op until RTX is wired). Delegates to the shared <see cref="VideoKeyFrameFeedback"/>, mirroring the
    /// single-stream video path. Runs on the bundle receive loop.
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        _keyFrameFeedback.OnRtcpPackets(packets);
    }

    // Delivers one packet in sequence order to the depacketiser. A discontinuity is a gap the reorder
    // window could not fill: the frame under assembly is torn, so reset before feeding on.
    private void DeliverOrdered(RtpPacket packet)
    {
        if (_hasDelivered && packet.SequenceNumber != unchecked((ushort)(_lastDeliveredSequence + 1)))
            _depacketiser.Reset();
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
        // Cancel any in-flight NACK/PLI send before releasing the send locks, so a feedback send never races
        // teardown (the SRTCP send path also fails closed on a disposed context).
        _lifetimeCts.Cancel();
        FrameReceived = null;
        KeyFrameRequested = null;
        _single?.Dispose();
        foreach (var layer in _layers.Values)
            layer.Dispose();
        _lifetimeCts.Dispose();
    }
}
