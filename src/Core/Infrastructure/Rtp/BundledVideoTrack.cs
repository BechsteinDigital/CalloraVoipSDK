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
/// single-stream video path drives it from the RTP receive loop. Sends are serialised so a frame's
/// packets never interleave with another frame's.
/// </remarks>
internal sealed class BundledVideoTrack : IDisposable
{
    private readonly string _mid;
    private readonly byte _payloadType;
    private readonly BundledOutboundPipeline _outbound;
    private readonly IVideoPacketiser _packetiser;
    private readonly IVideoDepacketiser _depacketiser;
    private readonly VideoReorderBuffer _reorderBuffer;
    private readonly ILogger<BundledVideoTrack> _logger;
    private readonly SemaphoreSlim _sendSync = new(1, 1);

    // RTP payload budget: MTU minus RTP/SRTP/extension overhead (mirrors the single-stream video path).
    private const int MaxRtpPayloadSize = 1200;

    // Receive-loop-only ordered-delivery state (reset the depacketiser on a genuine gap so a fragment of
    // a lost packet is never glued to the next frame).
    private bool _hasDelivered;
    private ushort _lastDeliveredSequence;

    /// <summary>Raised with a reassembled encoded frame, its RTP timestamp, and whether it is a key frame.</summary>
    public event Action<byte[], uint, bool>? FrameReceived;

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
        _payloadType = payloadType;
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        (_packetiser, _depacketiser) = VideoPayloadFormat.Create(codecName);
        _reorderBuffer = new VideoReorderBuffer(reorderWindowDepth);
    }

    /// <summary>
    /// Packetises one encoded frame and sends its payloads over the shared transport on the video MID.
    /// All payloads share <paramref name="rtpTimestamp"/> and are sent atomically (RFC 6184 §5.1 /
    /// RFC 7741 §4.1: the marker bit closes the frame on the last payload).
    /// </summary>
    public async Task SendFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default)
    {
        var payloads = _packetiser.Packetise(encodedFrame, MaxRtpPayloadSize);

        // Serialize whole frames: interleaving two frames' packets would corrupt the peer's reassembly.
        await _sendSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var payload in payloads)
                await _outbound.SendTimestampedAsync(
                        _mid, payload.Payload, payload.IsLastOfFrame, _payloadType, rtpTimestamp, ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            _sendSync.Release();
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

        try
        {
            FrameReceived?.Invoke(frame!, packet.Timestamp, isKeyFrame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled video FrameReceived handler.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FrameReceived = null;
        _sendSync.Dispose();
    }
}
