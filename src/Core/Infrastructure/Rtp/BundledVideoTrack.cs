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

    /// <summary>Total reassembled inbound frames delivered.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Total inbound key frames delivered.</summary>
    public long KeyFrames => Interlocked.Read(ref _keyFrames);

    /// <summary>Whether this track sends multiple simulcast encodings (RFC 8853).</summary>
    public bool IsSimulcast => _layers.Count > 0;

    /// <summary>The configured simulcast <c>a=rid</c> layer ids (empty for a non-simulcast track).</summary>
    public IReadOnlyCollection<string> SendRids => _layers.Keys.ToArray();

    /// <summary>Builds a non-simulcast video track (one RTP stream on the video MID).</summary>
    public BundledVideoTrack(
        string mid,
        string codecName,
        byte payloadType,
        BundledOutboundPipeline outbound,
        int reorderWindowDepth,
        ILogger<BundledVideoTrack> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var (packetiser, depacketiser) = VideoPayloadFormat.Create(codecName);
        _depacketiser = depacketiser;
        _reorderBuffer = new VideoReorderBuffer(reorderWindowDepth);
        _single = new BundledVideoSendEncoding(rid: null, payloadType, packetiser);
        _layers = new Dictionary<string, BundledVideoSendEncoding>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds a simulcast video track (RFC 8853): one RTP stream per <paramref name="rids"/> layer under
    /// the shared MID, each with its own packetiser and send lock. The receive path stays single-stream.
    /// </summary>
    public BundledVideoTrack(
        string mid,
        string codecName,
        byte payloadType,
        IReadOnlyList<string> rids,
        BundledOutboundPipeline outbound,
        int reorderWindowDepth,
        ILogger<BundledVideoTrack> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(rids);
        if (rids.Count == 0)
            throw new ArgumentException("A simulcast video track needs at least one rid.", nameof(rids));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reorderWindowDepth);
        _mid = mid;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
    /// packets into frames. Runs on the bundle receive loop (single consumer).
    /// </summary>
    public void OnRtpPacket(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        foreach (var released in _reorderBuffer.Insert(packet))
            DeliverOrdered(released);
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
        FrameReceived = null;
        _single?.Dispose();
        foreach (var layer in _layers.Values)
            layer.Dispose();
    }
}
