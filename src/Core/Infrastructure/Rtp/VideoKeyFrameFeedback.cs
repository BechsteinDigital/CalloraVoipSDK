using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// RTCP feedback for one video stream (RFC 4585/5104) over the stream's RTCP-mux channel.
/// Two directions: an inbound PLI/FIR asks us (the video sender) for a fresh reference
/// frame — surfaced as a keyframe-request callback for the encoder; detected inbound
/// packet loss makes us (the video receiver) report it to the peer — a Generic NACK naming
/// the lost sequence numbers (RFC 4585 §6.2.1) so the peer can retransmit, plus a
/// throttled PLI as the keyframe fallback. Feedback is only sent for the types the peer
/// advertised in SDP (<c>a=rtcp-fb</c>); FIR is honoured on receive but not generated.
/// </summary>
internal sealed class VideoKeyFrameFeedback
{
    private static readonly long PliThrottleTicks = Stopwatch.Frequency / 2; // 500 ms

    private readonly IRtcpPacketCodec _codec;
    private readonly uint _localSsrc;
    private readonly bool _remoteSupportsNack;
    private readonly bool _remoteSupportsPli;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendControl;
    private readonly Action _onKeyFrameRequested;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    private long _lastPliSentTimestamp = long.MinValue;

    public VideoKeyFrameFeedback(
        IRtcpPacketCodec codec,
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
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
        _remoteSupportsNack = remoteSupportsNack;
        _remoteSupportsPli = remoteSupportsPli;
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
    /// Reports detected inbound loss to the peer: a Generic NACK naming the missing
    /// sequence numbers when the peer supports it, and a throttled PLI as the keyframe
    /// fallback. Both are gated on the peer's advertised feedback — feedback it did not
    /// offer is never sent. Fire-and-forget: RTCP loss is tolerable.
    /// <paramref name="missingSequenceNumbers"/> must be ascending (as produced by the
    /// receiver's forward-gap detection); the NACK bitmask grouping relies on it.
    /// </summary>
    public void OnLoss(uint remoteSsrc, IReadOnlyList<ushort> missingSequenceNumbers)
    {
        ArgumentNullException.ThrowIfNull(missingSequenceNumbers);

        if (_remoteSupportsNack && missingSequenceNumbers.Count > 0)
        {
            var nack = new RtcpGenericNack
            {
                SenderSsrc = _localSsrc,
                MediaSsrc = remoteSsrc,
                Entries = BuildNackEntries(missingSequenceNumbers),
            };
            _ = SendAsync(nack, "NACK");
        }

        if (_remoteSupportsPli)
            RequestThrottledPli(remoteSsrc);
    }

    private void RequestThrottledPli(uint remoteSsrc)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPliSentTimestamp != long.MinValue && now - _lastPliSentTimestamp < PliThrottleTicks)
            return;

        _lastPliSentTimestamp = now;
        _ = SendAsync(new RtcpPictureLossIndication { SenderSsrc = _localSsrc, MediaSsrc = remoteSsrc }, "PLI");
    }

    private async Task SendAsync(RtcpPacket feedback, string kind)
    {
        try
        {
            await _sendControl(_codec.Encode([feedback]), _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Video {Kind} send aborted by session teardown.", kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send video {Kind} to the peer.", kind);
        }
    }

    // Groups lost sequence numbers into Generic NACK entries (RFC 4585 §6.2.1): each entry
    // is a base PID plus a 16-bit bitmask of the following packets (bit i = PID + i + 1).
    private static IReadOnlyList<RtcpNackEntry> BuildNackEntries(IReadOnlyList<ushort> missing)
    {
        var entries = new List<RtcpNackEntry>();
        var index = 0;
        while (index < missing.Count)
        {
            var pid = missing[index];
            ushort bitmask = 0;
            var next = index + 1;
            while (next < missing.Count)
            {
                var offset = (ushort)(missing[next] - pid);
                if (offset is < 1 or > 16)
                    break;
                bitmask |= (ushort)(1 << (offset - 1));
                next++;
            }

            entries.Add(new RtcpNackEntry { PacketId = pid, LostPacketBitmask = bitmask });
            index = next;
        }

        return entries;
    }
}
