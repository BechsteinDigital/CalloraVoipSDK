using System.Diagnostics;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using Microsoft.Extensions.Logging;

using CalloraVoipSdk.Core.Application.Media.Rtcp;

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
    private readonly bool _reducedSizeRtcp;
    private readonly bool _remoteSupportsNack;
    private readonly bool _remoteSupportsPli;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendControl;
    private readonly Action<uint> _onKeyFrameRequested;
    private readonly Action<IReadOnlyList<ushort>> _onRetransmitRequested;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    private long _lastPliSentTimestamp = long.MinValue;
    private long _nacksSent;
    private long _plisSent;

    /// <summary>Generic NACK feedback messages sent to the peer on detected inbound loss (RFC 4585 §6.2.1).</summary>
    public long NacksSent => Interlocked.Read(ref _nacksSent);

    /// <summary>PLI keyframe requests sent to the peer on detected inbound loss (RFC 4585 §6.3.1), after throttling.</summary>
    public long PlisSent => Interlocked.Read(ref _plisSent);

    public VideoKeyFrameFeedback(
        IRtcpPacketCodec codec,
        uint localSsrc,
        bool remoteSupportsNack,
        bool remoteSupportsPli,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendControl,
        Action<uint> onKeyFrameRequested,
        Action<IReadOnlyList<ushort>> onRetransmitRequested,
        ILogger logger,
        CancellationToken lifetime,
        bool reducedSizeRtcp = true)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sendControl);
        ArgumentNullException.ThrowIfNull(onKeyFrameRequested);
        ArgumentNullException.ThrowIfNull(onRetransmitRequested);
        ArgumentNullException.ThrowIfNull(logger);
        _codec = codec;
        _localSsrc = localSsrc;
        _reducedSizeRtcp = reducedSizeRtcp;
        _remoteSupportsNack = remoteSupportsNack;
        _remoteSupportsPli = remoteSupportsPli;
        _sendControl = sendControl;
        _onKeyFrameRequested = onKeyFrameRequested;
        _onRetransmitRequested = onRetransmitRequested;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Handles the decoded inbound RTCP compound (already SRTCP-unprotected and parsed once by the session): a
    /// PLI or FIR anywhere in it is treated as a keyframe request for this stream.
    /// </summary>
    public void OnRtcpPackets(IReadOnlyList<RtcpPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        // Any PLI/FIR here means "send a keyframe". The media SSRC it names is carried through rather than
        // discarded: on a bundled channel the caller has already filtered the compound to this track, and a
        // forwarding layer still needs to know WHICH of our streams was asked for — the whole point of #227.
        // A lenient peer that sets no usable SSRC yields 0, which is honest: unattributed, not misattributed.
        if (FirstKeyFrameRequestSsrc(packets) is { } namedSsrc)
        {
            _logger.LogDebug("Received a video keyframe request (PLI/FIR) naming media SSRC {Ssrc}.", namedSsrc);
            try
            {
                _onKeyFrameRequested(namedSsrc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in video KeyFrameRequested handler.");
            }
        }

        // (see FirstKeyFrameRequestSsrc below)

        // A Generic NACK names packets the peer lost — hand them to the retransmit path
        // (RFC 4588 RTX). The consumer resends whatever is still in its send buffer.
        var lost = packets.OfType<RtcpGenericNack>().SelectMany(n => n.LostSequenceNumbers()).ToArray();
        if (lost.Length > 0)
        {
            _logger.LogDebug("Received a video NACK for {Count} packet(s).", lost.Length);
            try
            {
                _onRetransmitRequested(lost);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in video retransmit handler.");
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
            Interlocked.Increment(ref _nacksSent);
            _ = SendAsync(nack, "NACK", _lifetime);
        }

        if (_remoteSupportsPli)
            RequestThrottledPli(remoteSsrc);
    }

    /// <summary>
    /// Sends a PLI keyframe request to the peer on the application's demand (RFC 4585 §6.3.1), independent of
    /// detected inbound loss — e.g. a newly attached renderer or a decoder reset needs a fresh reference frame.
    /// A no-op returning <see langword="false"/> when the peer did not advertise PLI, or when the shared 500 ms
    /// throttle still holds; otherwise sends the PLI, counts it (<see cref="PlisSent"/>), and returns
    /// <see langword="true"/> once the send completes. Thread-safe: may be called from any thread, concurrently
    /// with the receive-loop loss path, and shares that path's throttle.
    /// </summary>
    /// <param name="remoteSsrc">
    /// The media SSRC of the received video stream to name in the PLI (0 before the first inbound packet — a
    /// lenient peer honours a PLI on its dedicated video RTCP channel regardless of the named media SSRC).
    /// </param>
    /// <param name="cancellationToken">Cancels the RTCP send; linked with the stream lifetime.</param>
    /// <returns><see langword="true"/> when a PLI was sent; <see langword="false"/> on a no-op.</returns>
    public async ValueTask<bool> RequestKeyFrameAsync(uint remoteSsrc, CancellationToken cancellationToken = default)
    {
        if (!_remoteSupportsPli || !TryClaimPliSlot())
            return false;

        Interlocked.Increment(ref _plisSent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
        await SendAsync(
                new RtcpPictureLossIndication { SenderSsrc = _localSsrc, MediaSsrc = remoteSsrc }, "PLI", linked.Token)
            .ConfigureAwait(false);
        return true;
    }

    private void RequestThrottledPli(uint remoteSsrc)
    {
        if (!TryClaimPliSlot())
            return;

        Interlocked.Increment(ref _plisSent);
        _ = SendAsync(new RtcpPictureLossIndication { SenderSsrc = _localSsrc, MediaSsrc = remoteSsrc }, "PLI", _lifetime);
    }

    // Thread-safe 500 ms PLI throttle shared by the loss path (receive loop) and the app-driven request
    // (any thread, RequestKeyFrameAsync): a lock-free CAS claim so a burst across both paths still sends at
    // most one PLI per window. Exactly one caller claims a given window; the rest observe the live timestamp.
    private bool TryClaimPliSlot()
    {
        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            var last = Interlocked.Read(ref _lastPliSentTimestamp);
            if (last != long.MinValue && now - last < PliThrottleTicks)
                return false;
            if (Interlocked.CompareExchange(ref _lastPliSentTimestamp, now, last) == last)
                return true;
        }
    }

    private async Task SendAsync(RtcpPacket feedback, string kind, CancellationToken cancellationToken)
    {
        try
        {
            // #162 P2-3: ohne ausgehandeltes a=rtcp-rsize wird das Feedback in ein Compound gewickelt.
            var datagram = RtcpFeedbackFraming.Encode(_codec, feedback, _localSsrc, _reducedSizeRtcp);
            await _sendControl(datagram, cancellationToken).ConfigureAwait(false);
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

    // The media SSRC of the first key-frame request in the compound. PLI names it in the packet header
    // (RFC 4585 §6.3.1); FIR names its targets in the FCI entries instead (RFC 5104 §4.3.1), so the first
    // entry's SSRC is the one to report. Null when the compound holds no key-frame request at all.
    private static uint? FirstKeyFrameRequestSsrc(IReadOnlyList<RtcpPacket> packets)
    {
        foreach (var packet in packets)
        {
            switch (packet)
            {
                case RtcpPictureLossIndication pli:
                    return pli.MediaSsrc;
                case RtcpFullIntraRequest { Entries.Count: > 0 } fir:
                    return fir.Entries[0].MediaSsrc;
                case RtcpFullIntraRequest:
                    return 0u;   // a FIR with no entries names nothing; unattributed rather than misattributed
            }
        }

        return null;
    }
}
