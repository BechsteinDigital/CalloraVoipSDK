using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Assembles a full BUNDLE media session (ADR-011 B5, RFC 8843) from negotiated parameters: one shared
/// <see cref="BundledMediaTransport"/> (socket, B3-1) keyed by one <see cref="BundledDtlsKeying"/>
/// (DTLS-SRTP, B3-2) and kept alive by one <see cref="BundledIceControl"/> (ICE/consent, B3-3), carrying
/// an audio track and an optional video track (<see cref="BundledVideoTrack"/>, B4) over the inbound and
/// outbound pipelines (B2c-in). This is the object that ties the transport slices into one startable unit
/// — the internal composition a signalling-neutral WebRTC facade drives, or that the SDP negotiator
/// builds from a BUNDLE-negotiated offer/answer.
/// </summary>
internal sealed class BundledMediaSession : IAsyncDisposable
{
    private readonly BundledMediaTransport _transport;
    private readonly BundledOutboundPipeline _outbound;
    private readonly BundledDtlsKeying _dtls;
    private readonly BundledIceControl _ice;
    private readonly BundledVideoTrack? _video;
    private readonly string _audioMid;

    /// <summary>Raised with each decrypted inbound audio RTP packet.</summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    /// <summary>Raised when the shared DTLS handshake fails — media stays blocked (fail closed).</summary>
    public event Action? HandshakeFailed;

    public BundledMediaSession(
        BundledMediaSessionOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Audio);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _audioMid = options.Audio.Mid;

        // Inbound: demux the shared socket by the negotiated m-lines' payload types, route each MID.
        var payloadTypesByMid = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
        {
            [options.Audio.Mid] = new[] { (int)options.Audio.PayloadType },
        };
        if (options.Video is { } videoConfig)
            payloadTypesByMid[videoConfig.Mid] = new[] { (int)videoConfig.PayloadType };

        var router = new BundledTrackRouter(
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, payloadTypesByMid));
        router.RegisterTrack(options.Audio.Mid, packet => AudioReceived?.Invoke(packet));

        var inbound = new BundledInboundPipeline(
            router, new RtpPacketCodec(), loggerFactory.CreateLogger<BundledInboundPipeline>());

        _transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = options.LocalEndPoint, RemoteEndPoint = options.RemoteEndPoint },
            inbound, loggerFactory.CreateLogger<BundledMediaTransport>());

        // Outbound: a per-track sender for each m-line, stamping its MID.
        _outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), _transport, loggerFactory.CreateLogger<BundledOutboundPipeline>());
        _outbound.RegisterTrack(options.Audio.Mid, BuildOutboundTrack(options, options.Audio));

        if (options.Video is { } video)
        {
            _outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
            _video = new BundledVideoTrack(
                video.Mid, video.VideoCodecName ?? throw new ArgumentException("A video track must name its codec.", nameof(options)),
                video.PayloadType, _outbound, options.VideoReorderDepth, loggerFactory.CreateLogger<BundledVideoTrack>());
            _video.FrameReceived += (frame, timestamp, isKeyFrame) => VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame);
            router.RegisterTrack(video.Mid, _video.OnRtpPacket);
        }

        // One shared DTLS association keys every track; one shared ICE agent keeps the group alive.
        _dtls = new BundledDtlsKeying(
            options.DtlsIsClient, options.RemoteEndPoint, options.RemoteFingerprint,
            handshaker, certificate, inbound, _outbound, _transport,
            onHandshakeFailed: () => HandshakeFailed?.Invoke(), loggerFactory);

        _ice = new BundledIceControl(options.Ice, inbound, _transport.SendToAsync, loggerFactory);
    }

    private static BundledOutboundTrack BuildOutboundTrack(BundledMediaSessionOptions options, BundledTrackConfig track) =>
        new(track.Ssrc, track.PayloadType, track.SamplesPerPacket,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, options.MidExtensionId, track.Mid),
            options.InitialSequenceNumber, options.InitialTimestamp);

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>Whether this bundle carries a video track.</summary>
    public bool HasVideo => _video is not null;

    /// <summary>Starts the shared receive loop, the ICE consent loop, and the DTLS handshake.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _ice.Start();
        _dtls.Start(cancellationToken);
    }

    /// <summary>Sends one audio RTP payload on the audio track (suppressed until DTLS keys the transport).</summary>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, bool marker = false, CancellationToken cancellationToken = default)
        => _outbound.SendAsync(_audioMid, payload, marker, cancellationToken: cancellationToken);

    /// <summary>Packetises and sends one encoded video frame on the video track.</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video is { } video
            ? video.SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>
    /// Tears the session down: stops ICE and DTLS (closing the association, zeroing keys) before
    /// disposing the video track and finally the transport (which stops the receive loop and the socket).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _ice.DisposeAsync().ConfigureAwait(false);
        await _dtls.DisposeAsync().ConfigureAwait(false);
        _video?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
