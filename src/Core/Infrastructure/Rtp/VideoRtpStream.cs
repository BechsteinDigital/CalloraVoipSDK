using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Video RTP stream of one call leg (WebRTC phase 2): its own <see cref="RtpSession"/>
/// on the negotiated video port, the codec's packetisation (H.264 RFC 6184 / VP8
/// RFC 7741), and — on a DTLS-keyed call — its own DTLS association (RFC 5763: one per
/// m-line without BUNDLE) with the same local identity and role as audio. Fail-closed
/// like audio: a secure-negotiated leg never sends or accepts plaintext video. On an
/// RTX-negotiated leg (RFC 4588) inbound video passes through a bounded reorder window so a
/// retransmitted packet — delivered on the repair stream — can fill the gap that prompted the
/// NACK before playout; without RTX, packets feed the depacketiser in arrival order and a
/// sequence gap discards the frame under assembly. SDES-keyed video (RFC 4568) derives its
/// SRTP/SRTCP contexts from the video m-line's own key material, keyed from the first packet;
/// the SDP <c>a=crypto</c> negotiation that populates those params is the remaining follow-up.
/// Under SDES the RTX repair stream is not yet keyed — it stays fail-closed-silent until
/// per-stream SDES RTX keying lands (follow-up); DTLS-keyed RTX is unaffected.
/// </summary>
internal sealed class VideoRtpStream : IVideoMediaStream, IAsyncDisposable
{
    // Conservative RTP payload budget below a 1500 MTU: RTP header + SRTP tag headroom.
    private const int MaxRtpPayloadSize = 1200;

    // DECISION: inbound reorder/RTX-recovery window depth (packets). The reorder buffer releases
    // in-order packets immediately (no steady-state latency) and only holds behind a gap; depth
    // bounds how many packets are held — and thus how long — before it gives up on a missing
    // packet. 32 covers a ~1-RTT retransmit window plus realistic reordering. Only active on
    // RTX-negotiated legs.
    private const int ReorderWindowDepth = 32;

    private readonly RtpSession _rtp;
    private readonly DtlsMediaAttachment? _dtlsMedia;
    private readonly IVideoPacketiser _packetiser;
    private readonly IVideoDepacketiser _depacketiser;
    private readonly SemaphoreSlim _sendSync = new(1, 1);
    private readonly ILogger<VideoRtpStream> _logger;
    private readonly byte _payloadType;
    private readonly VideoKeyFrameFeedback _keyFrameFeedback;
    private readonly CancellationTokenSource _lifetimeCts = new();

    // RTX retransmission (RFC 4588): retains sent packets so an inbound NACK can be answered
    // by resending them on the repair stream. Null when RTX was not negotiated.
    private readonly RtpRetransmissionBuffer? _retransmitBuffer;
    private readonly byte _rtxPayloadType;
    private readonly uint _rtxSsrc;
    private int _rtxSequence;

    // Inbound reorder/RTX-recovery window (RFC 4588): present only when RTX is negotiated.
    // Normal and RTX-recovered packets both flow through it so a late retransmit can slot into
    // the gap it repairs before playout. Null when RTX was not negotiated (arrival-order
    // passthrough — the pre-RTX receive behaviour, byte-for-byte).
    private readonly VideoReorderBuffer? _reorderBuffer;

    // Arrival-order tracking, for fast NACK/PLI loss signalling (RFC 4585).
    private ushort _lastSequence;
    private bool _hasReceived;

    // Delivery-order tracking, for release-order discontinuity → depacketiser reset.
    private ushort _lastDeliveredSequence;
    private bool _hasDelivered;

    // Remote media SSRC, captured from inbound video to stamp RTX-recovered packets (RFC 4588 §4).
    private uint _remoteMediaSsrc;

    // SDES SRTP/SRTCP contexts (RFC 4568) for an SDES-keyed video m-line; null for a plain or
    // DTLS-keyed leg. Created and owned here (the RtpSession only borrows them), disposed —
    // zeroing the keys — on teardown.
    private readonly ISrtpContext? _sdesOutboundSrtp;
    private readonly ISrtpContext? _sdesInboundSrtp;
    private readonly ISrtcpContext? _sdesOutboundSrtcp;
    private readonly ISrtcpContext? _sdesInboundSrtcp;

    private int _disposed;

    private VideoRtpStream(
        CallVideoParameters video,
        CallMediaParameters parameters,
        ILoggerFactory loggerFactory,
        IDtlsSrtpHandshaker? dtlsHandshaker,
        DtlsCertificate? dtlsCertificate)
    {
        _logger = loggerFactory.CreateLogger<VideoRtpStream>();
        _payloadType = (byte)video.PayloadType;
        CodecName = video.CodecName;
        (_packetiser, _depacketiser) = CreatePayloadFormat(video.CodecName);

        // SDES keying (RFC 4568) for the video m-line: build the SRTP/SRTCP contexts from the
        // video stream's own negotiated key material and hand them to the session (which borrows
        // them). All-null for a plain or DTLS-keyed leg — DTLS installs its contexts post-
        // handshake — so the primary path is unchanged there. Mutually exclusive with DTLS.
        (_sdesOutboundSrtp, _sdesInboundSrtp, _sdesOutboundSrtcp, _sdesInboundSrtcp) =
            SdesMediaCryptoContextFactory.TryCreate(
                video.SrtpSuite, video.SrtpLocalKeyParams, video.SrtpRemoteKeyParams, _logger);

        _rtp = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = video.LocalEndPoint,
                RemoteEndPoint = video.RemoteEndPoint,
                PayloadType = _payloadType,
                ClockRate = video.ClockRate,
                // Video frames carry explicit timestamps; the audio-style per-packet
                // advance is unused. One nominal frame interval keeps RTCP maths sane.
                SamplesPerPacket = video.ClockRate / 30,
                OutboundSrtp = _sdesOutboundSrtp,
                InboundSrtp = _sdesInboundSrtp,
                OutboundSrtcp = _sdesOutboundSrtcp,
                InboundSrtcp = _sdesInboundSrtcp,
                RequireEncryptedMedia = parameters.IsSrtpNegotiated || parameters.IsDtlsNegotiated,
            },
            new RtpPacketCodec(),
            loggerFactory.CreateLogger<RtpSession>());
        _rtp.PacketReceived += OnPacketReceived;

        // RTX repair stream (RFC 4588): configure the negotiated payload type so inbound RTX
        // is demultiplexed onto its own SRTP context (never disturbing the primary replay
        // window), and retain sent packets so an inbound NACK can be answered by resending.
        if (video.RtxPayloadType is { } rtxPt)
        {
            _rtxPayloadType = (byte)rtxPt;
            _rtxSsrc = unchecked((uint)Random.Shared.Next());
            _retransmitBuffer = new RtpRetransmissionBuffer();
            _rtp.ConfigureSecondaryStream(_rtxPayloadType);
            _rtp.PacketSent += _retransmitBuffer.Store;

            // Inbound recovery: a reorder window absorbs network reordering and lets an RTX
            // retransmit (delivered via SecondaryPacketReceived) fill its gap before playout.
            _reorderBuffer = new VideoReorderBuffer(ReorderWindowDepth);
            _rtp.SecondaryPacketReceived += OnRtxPacketReceived;
        }

        // Keyframe feedback (RFC 4585/5104) over the video RTCP-mux channel: inbound PLI/FIR
        // → KeyFrameRequested (for the encoder); inbound loss → NACK/PLI to the peer; inbound
        // NACK → RTX retransmit of the requested packets.
        _keyFrameFeedback = new VideoKeyFrameFeedback(
            new RtcpPacketCodec(), _rtp.LocalSsrc,
            video.RemoteSupportsNack, video.RemoteSupportsPli, _rtp.SendControlAsync,
            () => KeyFrameRequested?.Invoke(),
            OnRetransmitRequested,
            loggerFactory.CreateLogger<VideoKeyFrameFeedback>(), _lifetimeCts.Token);
        _rtp.ControlPacketReceived += _keyFrameFeedback.OnControlDatagram;

        // DTLS-SRTP for the video 5-tuple: same identity, role, and peer fingerprint as
        // audio (the answer commits one a=setup per session in this SDK), own handshake.
        // When RTX is negotiated, the handshake also keys the repair stream's own contexts.
        _dtlsMedia = DtlsMediaAttachment.TryCreate(
            parameters, dtlsHandshaker, dtlsCertificate, _rtp.SendRawAsync,
            _rtp.InstallSecurityContexts, _rtp.StopTransmission, loggerFactory,
            remoteEndPointOverride: video.RemoteEndPoint,
            onSecondaryContextsReady: _retransmitBuffer is null ? null : _rtp.InstallSecondarySecurityContexts);
        if (_dtlsMedia is not null)
            _rtp.DtlsPacketReceived += _dtlsMedia.OnDtlsPacketReceived;
    }

    // Answers an inbound NACK (RFC 4585) by resending the requested packets on the RTX repair
    // stream (RFC 4588): each still in the buffer is re-wrapped with the rtx payload type,
    // SSRC, and a fresh rtx sequence number, then sent on the secondary stream. Runs on the
    // RTCP receive-loop thread. A packet no longer in the window is simply not resent.
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

    private async Task SendRtxAsync(Packets.RtpPacket rtx)
    {
        try
        {
            await _rtp.SendSecondaryAsync(rtx, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Teardown while retransmitting — nothing to recover.
            _logger.LogTrace("RTX retransmission aborted by session teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send an RTX retransmission.");
        }
    }

    /// <inheritdoc />
    public string CodecName { get; }

    /// <inheritdoc />
    public int PayloadType => _payloadType;

    /// <inheritdoc />
    public event Action<byte[], uint>? FrameReceived;

    /// <inheritdoc />
    public event Action? KeyFrameRequested;

    /// <summary>
    /// Creates the video stream for a leg that negotiated video; <see langword="null"/>
    /// for audio-only legs. Throws when the negotiated codec has no payload format or a
    /// DTLS-keyed leg lacks its DTLS dependencies (fail closed, validated up front).
    /// </summary>
    public static VideoRtpStream? TryCreate(
        CallMediaParameters parameters,
        ILoggerFactory loggerFactory,
        IDtlsSrtpHandshaker? dtlsHandshaker,
        DtlsCertificate? dtlsCertificate)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (parameters.Video is not { } video)
            return null;

        return new VideoRtpStream(video, parameters, loggerFactory, dtlsHandshaker, dtlsCertificate);
    }

    /// <summary>Starts the RTP receive loop and, on DTLS-keyed legs, the handshake.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        _ = _rtp.StartAsync(cancellationToken);
        _dtlsMedia?.Start(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendFrameAsync(
        ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken ct = default)
    {
        var payloads = _packetiser.Packetise(encodedFrame, MaxRtpPayloadSize);

        // Serialize whole frames: interleaved packets of two frames would corrupt the
        // peer's reassembly (all packets of a frame must be consecutive per timestamp).
        await _sendSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var payload in payloads)
            {
                await _rtp.SendTimestampedAsync(
                        payload.Payload, payload.IsLastOfFrame, _payloadType, rtpTimestamp, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendSync.Release();
        }
    }

    // Runs on the RTP receive-loop thread — the single consumer the depacketiser needs.
    private void OnPacketReceived(object? sender, Packets.RtpPacket packet)
    {
        if (packet.PayloadType != _payloadType)
            return;

        _remoteMediaSsrc = packet.Ssrc;

        // Arrival-order loss signalling (RFC 4585): on a genuine forward gap request retransmit
        // at once — before the reorder window can slide past it — and request a keyframe. A
        // reorder or duplicate is not loss (LossReport returns null): the reorder window corrects
        // it downstream, so it raises neither a NACK nor a PLI. Ordered delivery and the
        // depacketiser reset are handled downstream, not here.
        if (_hasReceived && LossReport(_lastSequence, packet.SequenceNumber) is { } missing)
            _keyFrameFeedback.OnLoss(packet.Ssrc, missing);
        _lastSequence = packet.SequenceNumber;
        _hasReceived = true;

        Enqueue(packet);
    }

    // Recovers an inbound RTX repair packet (RFC 4588 §4) and feeds the original into the
    // reorder window, where it can fill the gap that prompted the NACK — unless the window has
    // already slid past it. Runs on the RTP receive-loop thread, the same thread as primary
    // video, so reorder/delivery state needs no extra synchronisation.
    private void OnRtxPacketReceived(Packets.RtpPacket rtx)
    {
        if (_reorderBuffer is null)
            return;

        // _remoteMediaSsrc is captured from the first primary video packet; an RTX arriving
        // before any primary stamps the recovered packet with 0. Cosmetic only — the reorder
        // buffer keys on sequence number and the depacketiser ignores SSRC.
        if (!RtxPacketFactory.TryDecapsulate(rtx, _payloadType, _remoteMediaSsrc, out var original))
        {
            _logger.LogDebug("Dropping RTX packet too short to carry an original sequence number.");
            return;
        }

        Enqueue(original!);
    }

    // Feeds one video packet (freshly received or RTX-recovered) toward the depacketiser. With
    // RTX negotiated the reorder window releases in ascending sequence order (letting a late
    // retransmit slot into its gap); without it the packet passes straight through in arrival
    // order, preserving the pre-RTX behaviour exactly.
    private void Enqueue(Packets.RtpPacket packet)
    {
        if (_reorderBuffer is null)
        {
            DeliverOrdered(packet);
            return;
        }

        foreach (var released in _reorderBuffer.Insert(packet))
            DeliverOrdered(released);
    }

    // Delivers one video packet in sequence order to the depacketiser. A discontinuity here is
    // a gap the reorder window (or arrival order, without RTX) could not fill: the frame under
    // assembly is torn, so reset before feeding on. The loss was already signalled on arrival.
    private void DeliverOrdered(Packets.RtpPacket packet)
    {
        if (_hasDelivered && packet.SequenceNumber != unchecked((ushort)(_lastDeliveredSequence + 1)))
            _depacketiser.Reset();
        _lastDeliveredSequence = packet.SequenceNumber;
        _hasDelivered = true;

        if (!_depacketiser.TryProcess(packet.Payload, packet.Timestamp, packet.Marker, out var frame))
            return;

        try
        {
            FrameReceived?.Invoke(frame!, packet.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in video FrameReceived handler.");
        }
    }

    /// <summary>
    /// Stops transmission, closes the DTLS association (close_notify before the socket
    /// goes down), and disposes the RTP session — mirroring the audio teardown order.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _rtp.PacketReceived -= OnPacketReceived;
        _rtp.ControlPacketReceived -= _keyFrameFeedback.OnControlDatagram;
        if (_retransmitBuffer is not null)
            _rtp.PacketSent -= _retransmitBuffer.Store;
        if (_reorderBuffer is not null)
            _rtp.SecondaryPacketReceived -= OnRtxPacketReceived;
        if (_dtlsMedia is not null)
        {
            _rtp.StopTransmission();
            _rtp.DtlsPacketReceived -= _dtlsMedia.OnDtlsPacketReceived;
            await _dtlsMedia.DisposeAsync().ConfigureAwait(false);
        }

        await _rtp.DisposeAsync().ConfigureAwait(false);

        // Zero the SDES key material now the session has stopped using it (owner disposes).
        _sdesOutboundSrtp?.Dispose();
        _sdesInboundSrtp?.Dispose();
        _sdesOutboundSrtcp?.Dispose();
        _sdesInboundSrtcp?.Dispose();

        FrameReceived = null;
        KeyFrameRequested = null;
        _sendSync.Dispose();
        _lifetimeCts.Dispose();
    }

    // Beyond this many missing packets a NACK is pointless — the loss is better recovered
    // with a keyframe (PLI), so we stop enumerating and let the throttled PLI carry it.
    private const int MaxEnumeratedLoss = 256;

    // A backward sequence step (a reorder) wraps the forward distance to at least this value;
    // treated as reordering, not loss. Half the 16-bit space is the reorder/loss boundary.
    private const int ReorderBoundary = 0x8000;

    /// <summary>
    /// Classifies a newly arrived sequence number against the last one for loss reporting:
    /// <list type="bullet">
    /// <item><see langword="null"/> — in-order, a duplicate, or a reorder (a backward step is
    /// not loss): nothing to report.</item>
    /// <item>empty — a forward loss burst larger than <see cref="MaxEnumeratedLoss"/>: report as
    /// a PLI only (naming every packet in a NACK is pointless; a keyframe recovers faster).</item>
    /// <item>a list — the missing sequence numbers of a small forward gap: NACK them (plus PLI).</item>
    /// </list>
    /// Suppressing the reorder case is what stops a reordered packet from provoking a spurious
    /// NACK and keyframe request now that the reorder window corrects reordering downstream.
    /// A forward loss burst of at least half the sequence space (≥ <see cref="ReorderBoundary"/>)
    /// is indistinguishable from a backward step under 16-bit serial-number arithmetic and is
    /// therefore treated as a reorder — a pathological case that never arises in a live stream.
    /// </summary>
    internal static IReadOnlyList<ushort>? LossReport(ushort last, ushort current)
    {
        var gap = (ushort)(current - last); // forward distance; a reorder wraps to a large value
        if (gap < 2 || gap >= ReorderBoundary)
            return null; // in-order (1), duplicate (0), or reorder (backward step)

        if (gap > MaxEnumeratedLoss)
            return Array.Empty<ushort>(); // forward loss too large to enumerate → PLI only

        var missing = new ushort[gap - 1];
        for (var i = 0; i < missing.Length; i++)
            missing[i] = (ushort)(last + i + 1);
        return missing;
    }

    private static (IVideoPacketiser Packetiser, IVideoDepacketiser Depacketiser) CreatePayloadFormat(
        string codecName) => codecName.ToUpperInvariant() switch
    {
        "VP8" => (new Vp8Packetiser(), new Vp8Depacketiser()),
        "H264" => (new H264Packetiser(), new H264Depacketiser()),
        _ => throw new InvalidOperationException(
            $"Negotiated video codec '{codecName}' has no RTP payload format implementation."),
    };
}
