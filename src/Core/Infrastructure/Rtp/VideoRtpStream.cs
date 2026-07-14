using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
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

        // DTLS-SRTP for the video 5-tuple: same identity, role, and peer fingerprint as
        // audio (the answer commits one a=setup per session in this SDK), own handshake.
        _dtlsMedia = DtlsMediaAttachment.TryCreate(
            parameters, dtlsHandshaker, dtlsCertificate, _rtp.SendRawAsync,
            _rtp.InstallSecurityContexts, _rtp.StopTransmission, loggerFactory,
            remoteEndPointOverride: video.RemoteEndPoint);
        if (_dtlsMedia is not null)
            _rtp.DtlsPacketReceived += _dtlsMedia.OnDtlsPacketReceived;
    }

    /// <inheritdoc />
    public string CodecName { get; }

    /// <inheritdoc />
    public int PayloadType => _payloadType;

    /// <inheritdoc />
    public event Action<byte[], uint>? FrameReceived;

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

        // Sequence gap: a fragment is missing — discard the frame under assembly so a
        // torn frame is never delivered (no retransmission until the feedback phase).
        if (_hasReceived && packet.SequenceNumber != unchecked((ushort)(_lastSequence + 1)))
            _depacketiser.Reset();
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

        _rtp.PacketReceived -= OnPacketReceived;
        if (_dtlsMedia is not null)
        {
            _rtp.StopTransmission();
            _rtp.DtlsPacketReceived -= _dtlsMedia.OnDtlsPacketReceived;
            await _dtlsMedia.DisposeAsync().ConfigureAwait(false);
        }

        await _rtp.DisposeAsync().ConfigureAwait(false);
        FrameReceived = null;
        _sendSync.Dispose();
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
