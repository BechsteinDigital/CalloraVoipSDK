using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Video RTP stream of one call leg (WebRTC phase 2): its own <see cref="RtpSession"/>
/// on the negotiated video port, the codec's packetisation (H.264 RFC 6184 / VP8
/// RFC 7741), and — on a DTLS-keyed call — its own DTLS association (RFC 5763: one per
/// m-line without BUNDLE) with the same local identity and role as audio. Fail-closed
/// like audio: a secure-negotiated leg never sends or accepts plaintext video. No video
/// jitter buffer yet — packets feed the depacketiser in arrival order and any sequence
/// gap discards the frame under assembly (loss recovery is the RTCP-feedback phase).
/// SDES-keyed video is not wired: under SDES the video sub-stream has no SRTP context
/// and stays fail-closed-silent until per-m-line SDES keying lands (follow-up).
/// </summary>
internal sealed class VideoRtpStream : IVideoMediaStream, IAsyncDisposable
{
    // Conservative RTP payload budget below a 1500 MTU: RTP header + SRTP tag headroom.
    private const int MaxRtpPayloadSize = 1200;

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

    private ushort _lastSequence;
    private bool _hasReceived;
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

        // Sequence gap: a fragment is missing — discard the frame under assembly so a torn
        // frame is never delivered, and report the loss (RFC 4585 NACK for the missing
        // packets, plus a throttled PLI) so decoding recovers instead of stalling.
        if (_hasReceived && packet.SequenceNumber != unchecked((ushort)(_lastSequence + 1)))
        {
            _depacketiser.Reset();
            _keyFrameFeedback.OnLoss(packet.Ssrc, MissingBetween(_lastSequence, packet.SequenceNumber));
        }
        _lastSequence = packet.SequenceNumber;
        _hasReceived = true;

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
        if (_dtlsMedia is not null)
        {
            _rtp.StopTransmission();
            _rtp.DtlsPacketReceived -= _dtlsMedia.OnDtlsPacketReceived;
            await _dtlsMedia.DisposeAsync().ConfigureAwait(false);
        }

        await _rtp.DisposeAsync().ConfigureAwait(false);
        FrameReceived = null;
        KeyFrameRequested = null;
        _sendSync.Dispose();
        _lifetimeCts.Dispose();
    }

    // Beyond this many missing packets a NACK is pointless — the loss is better recovered
    // with a keyframe (PLI), so we stop enumerating and let the throttled PLI carry it.
    private const int MaxEnumeratedLoss = 256;

    /// <summary>
    /// The sequence numbers missing between the last delivered packet and a newly arrived
    /// one, for a forward gap. Empty for a duplicate/reorder (the delta is not a small
    /// forward step) or a loss burst larger than <see cref="MaxEnumeratedLoss"/>.
    /// </summary>
    private static IReadOnlyList<ushort> MissingBetween(ushort last, ushort current)
    {
        var gap = (ushort)(current - last); // forward distance, wraps naturally
        if (gap < 2 || gap > MaxEnumeratedLoss)
            return [];

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
