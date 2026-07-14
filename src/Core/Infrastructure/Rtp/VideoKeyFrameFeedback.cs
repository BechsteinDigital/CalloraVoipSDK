using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Keyframe-request feedback for one video stream (RFC 4585 PLI, RFC 5104 FIR). Two
/// directions over the stream's RTCP-mux channel: an inbound PLI/FIR asks us (the video
/// sender) for a fresh reference frame — surfaced as a keyframe-request callback for the
/// encoder; a detected inbound packet loss makes us (the video receiver) send a PLI to
/// the peer, throttled so a burst of loss does not flood RTCP (baresip uses a 500 ms
/// picture-update interval). FIR is honoured on receive but not generated (PLI is the
/// lighter request and enough for a single stream).
/// </summary>
internal sealed class VideoKeyFrameFeedback
{
    private static readonly long PliThrottleTicks = Stopwatch.Frequency / 2; // 500 ms

    private readonly IRtcpPacketCodec _codec;
    private readonly uint _localSsrc;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendControl;
    private readonly Action _onKeyFrameRequested;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    private long _lastPliSentTimestamp = long.MinValue;

    public VideoKeyFrameFeedback(
        IRtcpPacketCodec codec,
        uint localSsrc,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendControl,
        Action onKeyFrameRequested,
        ILogger logger,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendControl);
        ArgumentNullException.ThrowIfNull(onKeyFrameRequested);
        ArgumentNullException.ThrowIfNull(logger);
        _codec = codec;
        _localSsrc = localSsrc;
        _sendControl = sendControl;
        _onKeyFrameRequested = onKeyFrameRequested;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Handles an inbound RTCP datagram (already SRTCP-unprotected): a PLI or FIR anywhere
    /// in the compound packet is treated as a keyframe request for this stream. Malformed
    /// datagrams are dropped — RTCP feedback must never break the receive path.
    /// </summary>
    public void OnControlDatagram(byte[] datagram)
    {
        IReadOnlyList<RtcpPacket> packets;
        try
        {
            packets = _codec.Decode(datagram);
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Dropping malformed inbound video RTCP datagram.");
            return;
        }

        // A dedicated single-stream video RTCP channel: any PLI/FIR here means "send a
        // keyframe", regardless of the media SSRC a lenient peer may have set.
        if (packets.Any(p => p is RtcpPictureLossIndication or RtcpFullIntraRequest))
        {
            _logger.LogDebug("Received a video keyframe request (PLI/FIR).");
            try
            {
                _onKeyFrameRequested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in video KeyFrameRequested handler.");
            }
        }
    }

    /// <summary>
    /// Requests a keyframe from the peer by sending a PLI for its media stream, throttled
    /// to at most one per 500 ms. Called on an inbound sequence gap. Fire-and-forget:
    /// RTCP loss is tolerable and the throttle re-fires the request.
    /// </summary>
    public void RequestRemoteKeyFrame(uint remoteSsrc)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPliSentTimestamp != long.MinValue && now - _lastPliSentTimestamp < PliThrottleTicks)
            return;

        _lastPliSentTimestamp = now;
        _ = SendPliAsync(remoteSsrc);
    }

    private async Task SendPliAsync(uint remoteSsrc)
    {
        try
        {
            var pli = new RtcpPictureLossIndication { SenderSsrc = _localSsrc, MediaSsrc = remoteSsrc };
            var datagram = _codec.Encode([pli]);
            await _sendControl(datagram, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session teardown while sending — nothing to recover.
            _logger.LogTrace("Video PLI send aborted by session teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send video PLI to the peer.");
        }
    }
}
