using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
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
    private readonly BundledInboundPipeline _inbound;
    private readonly BundledDtlsKeying _dtls;
    private readonly BundledIceControl _ice;
    private readonly BundledVideoTrack? _video;
    private readonly string _audioMid;
    private readonly uint _audioSsrc;
    private readonly bool _audioSendEnabled;
    private readonly ILogger<BundledMediaSession> _logger;

    /// <summary>Raised with each decrypted inbound audio RTP packet.</summary>
    public event Action<RtpPacket>? AudioReceived;

    /// <summary>Raised with each reassembled inbound video frame (frame, RTP timestamp, is-key-frame).</summary>
    public event Action<byte[], uint, bool>? VideoFrameReceived;

    /// <summary>Raised when the shared DTLS handshake fails — media stays blocked (fail closed).</summary>
    public event Action? HandshakeFailed;

    /// <summary>Raised when the shared DTLS handshake installs the SRTP keys and media can flow.</summary>
    public event Action? Connected;

    /// <summary>Raised once when RFC 7675 ICE consent is lost for the shared 5-tuple.</summary>
    public event Action? MediaConsentLost;

    /// <summary>Raised on a transient consent miss still inside the consent window (RFC 7675).</summary>
    public event Action? MediaConnectivityDegraded;

    /// <summary>Raised when a consent check is answered again after a degrade.</summary>
    public event Action? MediaConnectivityRecovered;

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
        _audioSendEnabled = options.AudioSendEnabled;
        _logger = loggerFactory.CreateLogger<BundledMediaSession>();

        // Inbound: demux the shared socket by the negotiated m-lines' payload types, route each MID.
        var payloadTypesByMid = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
        {
            [options.Audio.Mid] = new[] { (int)options.Audio.PayloadType },
        };
        if (options.Video is { } videoConfig)
            payloadTypesByMid[videoConfig.Mid] = new[] { (int)videoConfig.PayloadType };

        var router = new BundledTrackRouter(
            BundledRtpDemultiplexerFactory.Create(options.MidExtensionId, payloadTypesByMid));
        router.RegisterTrack(options.Audio.Mid, RaiseAudioReceived);

        _inbound = new BundledInboundPipeline(
            router, new RtpPacketCodec(), loggerFactory.CreateLogger<BundledInboundPipeline>());

        _transport = new BundledMediaTransport(
            new BundledMediaTransportOptions { LocalEndPoint = options.LocalEndPoint, RemoteEndPoint = options.RemoteEndPoint },
            _inbound, loggerFactory.CreateLogger<BundledMediaTransport>(), options.PreBoundSocket);

        // A relay ICE local candidate rides the same shared socket. Now that the socket exists, the injected
        // (TURN-aware) factory builds the indication channel + control transactor + relay send path from the
        // transport's targeted send; the transport unwraps relayed inbound datagrams and feeds control responses
        // (SetIndicationRelay), and the relay send path becomes the ICE agent's relay candidate below. Null
        // (no gathered allocation) leaves the transport direct-only.
        var relayBinding = options.RelayIceBindingFactory?.Invoke(_transport.SendToAsync);
        if (relayBinding is not null)
            _transport.SetIndicationRelay(relayBinding.Indication, relayBinding.OnControl);

        // Outbound: a per-track sender for each m-line, stamping its MID.
        _outbound = new BundledOutboundPipeline(
            new RtpPacketCodec(), _transport, loggerFactory.CreateLogger<BundledOutboundPipeline>());
        _outbound.RegisterTrack(options.Audio.Mid, BuildOutboundTrack(options, options.Audio));

        if (options.Video is { } video)
        {
            var codecName = video.VideoCodecName
                ?? throw new ArgumentException("A video track must name its codec.", nameof(options));

            if (video.Encodings.Count > 0)
            {
                // Send-side simulcast (RFC 8853): one outbound RTP stream per a=rid layer under the shared
                // MID, each on its own SSRC with the negotiated RID header extension (RFC 8852) stamped.
                var ridExtensionId = options.RidExtensionId ?? throw new ArgumentException(
                    "A simulcast video track needs a negotiated RID header-extension id.", nameof(options));
                foreach (var encoding in video.Encodings)
                    _outbound.RegisterTrack(video.Mid, encoding.Rid,
                        BuildEncodingTrack(options, video.Mid, encoding.Ssrc, video.PayloadType, encoding.Rid, ridExtensionId));

                _video = new BundledVideoTrack(
                    video.Mid, codecName, video.PayloadType,
                    video.Encodings.Select(e => e.Rid).ToArray(),
                    _outbound, options.VideoReorderDepth, loggerFactory.CreateLogger<BundledVideoTrack>());
            }
            else
            {
                _outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
                _video = new BundledVideoTrack(
                    video.Mid, codecName, video.PayloadType, _outbound, options.VideoReorderDepth,
                    loggerFactory.CreateLogger<BundledVideoTrack>());
            }

            _video.FrameReceived += (frame, timestamp, isKeyFrame) => VideoFrameReceived?.Invoke(frame, timestamp, isKeyFrame);
            router.RegisterTrack(video.Mid, _video.OnRtpPacket);
        }

        // One shared DTLS association keys every track; one shared ICE agent keeps the group alive.
        _dtls = new BundledDtlsKeying(
            options.DtlsIsClient, options.RemoteEndPoint, options.RemoteFingerprint,
            handshaker, certificate, _inbound, _outbound, _transport,
            onHandshakeFailed: () => HandshakeFailed?.Invoke(), loggerFactory,
            onKeysInstalled: () => Connected?.Invoke());

        _ice = new BundledIceControl(
            options.Ice, _inbound, _transport.SendToAsync, loggerFactory,
            onConsentLost: () => MediaConsentLost?.Invoke(),
            onConnectivityDegraded: () => MediaConnectivityDegraded?.Invoke(),
            onConnectivityRecovered: () => MediaConnectivityRecovered?.Invoke(),
            // A nominated ICE pair (RFC 8445 §8) becomes the transport's send target AND the DTLS remote,
            // so the DTLS handshake's inbound source filter follows the connectivity-checked pair.
            onPairNominated: OnPairNominated,
            // The relay send path (when a TURN allocation was gathered) becomes the ICE agent's relay local
            // candidate — checked alongside the direct one, direct-preferred by pair priority.
            relaySend: relayBinding?.RelaySend);

        _audioSsrc = options.Audio.Ssrc;
    }

    // Dispatches inbound audio to subscribers on the receive loop; a throwing subscriber must not tear
    // down the shared receive loop (the video path is guarded the same way inside BundledVideoTrack).
    private void RaiseAudioReceived(RtpPacket packet)
    {
        try
        {
            AudioReceived?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled audio AudioReceived handler.");
        }
    }

    private static BundledOutboundTrack BuildOutboundTrack(BundledMediaSessionOptions options, BundledTrackConfig track) =>
        new(track.Ssrc, track.PayloadType, track.SamplesPerPacket,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, options.MidExtensionId, track.Mid),
            options.InitialSequenceNumber, options.InitialTimestamp);

    // One simulcast encoding's outbound stream: its own SSRC, the shared video payload type, and a stamper
    // that marks every packet with the MID and this encoding's RID (RFC 8852). Video packets carry an
    // explicit frame timestamp, so the timestamp cursor never advances (samplesPerPacket: 0).
    private static BundledOutboundTrack BuildEncodingTrack(
        BundledMediaSessionOptions options, string mid, uint ssrc, byte payloadType, string rid, byte ridExtensionId) =>
        new(ssrc, payloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                transportWideCcExtensionId: null, options.MidExtensionId, mid, ridExtensionId, rid),
            options.InitialSequenceNumber, options.InitialTimestamp);

    /// <summary>The endpoint the shared socket is bound to (the actual port after an ephemeral bind).</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>The local audio track's synchronisation source.</summary>
    public uint AudioSsrc => _audioSsrc;

    /// <summary>Whether this bundle carries a video track.</summary>
    public bool HasVideo => _video is not null;

    /// <summary>
    /// Whether outbound audio is sent. False when the negotiated directions do not carry audio from this peer
    /// to the remote (a send-only/inactive remote answer, or a local side that does not send); the audio
    /// m-line still anchors the transport and inbound audio is still received.
    /// </summary>
    public bool AudioSendEnabled => _audioSendEnabled;

    /// <summary>Whether the video track sends multiple simulcast encodings (RFC 8853).</summary>
    public bool VideoIsSimulcast => _video?.IsSimulcast ?? false;

    /// <summary>The configured simulcast <c>a=rid</c> layer ids, or empty when not simulcasting.</summary>
    public IReadOnlyCollection<string> VideoSendRids => _video?.SendRids ?? [];

    /// <summary>The remote media endpoint the shared transport sends to, or null before one is set.</summary>
    public IPEndPoint? RemoteEndPoint => _transport.RemoteEndPoint;

    /// <summary>
    /// Points the shared transport at a new remote media endpoint (a trickled ICE candidate, RFC 8838).
    /// Thread-safe; the symmetric transport still latches the peer's real source on the next received packet.
    /// </summary>
    public void SetRemoteEndPoint(IPEndPoint remoteEndPoint) => _transport.SetRemoteEndPoint(remoteEndPoint);

    /// <summary>
    /// Adds a trickled remote ICE candidate (RFC 8838) to the connectivity-check list instead of trusting it
    /// by raw priority: the controlling agent checks it and, if it answers and beats the current pair,
    /// nominates it (redirecting the transport send target and DTLS). No-op on a controlled agent or without ICE.
    /// </summary>
    /// <param name="remoteEndPoint">The candidate's transport address.</param>
    /// <param name="priority">The candidate's ICE priority (RFC 8445 §5.1.2.1), used to order checks.</param>
    public void AddRemoteCandidate(IPEndPoint remoteEndPoint, long priority)
        => _ice.AddRemoteCandidate(new IceRemoteCandidate(remoteEndPoint, priority));

    // A connectivity-checked ICE nomination (RFC 8445 §8) redirects the whole 5-tuple onto the nominated
    // pair: the transport's send target and the DTLS association's inbound source filter both follow it, so
    // the handshake completes against the checked candidate rather than the initial SDP endpoint.
    private void OnPairNominated(IPEndPoint remoteEndPoint)
    {
        _transport.SetRemoteEndPoint(remoteEndPoint);
        _dtls.SetRemoteEndPoint(remoteEndPoint);
    }

    /// <summary>Point-in-time transport counters aggregated from the outbound and inbound pipelines.</summary>
    public BundledMediaStats SnapshotStats() => new(
        _outbound.PacketsSent, _outbound.BytesSent, _outbound.SuppressedSends,
        _inbound.RtpPacketsReceived, _inbound.RtpBytesReceived, _inbound.DroppedDatagrams,
        _video?.FramesReceived, _video?.KeyFrames);

    /// <summary>Starts the shared receive loop, the ICE consent loop, and the DTLS handshake.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _ice.Start();
        _dtls.Start(cancellationToken);
    }

    /// <summary>
    /// Sends one audio RTP payload on the audio track (suppressed until DTLS keys the transport). A no-op when
    /// the negotiation did not enable outbound audio (<see cref="AudioSendEnabled"/> is false) — the remote
    /// will not receive it, so nothing is streamed even if the caller keeps feeding audio.
    /// </summary>
    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, bool marker = false, CancellationToken cancellationToken = default)
        => _audioSendEnabled
            ? _outbound.SendAsync(_audioMid, payload, marker, cancellationToken: cancellationToken)
            : default;

    /// <summary>Packetises and sends one encoded video frame on the (non-simulcast) video track.</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track, or it is simulcast.</exception>
    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video is { } video
            ? video.SendFrameAsync(encodedFrame, rtpTimestamp, cancellationToken)
            : throw new InvalidOperationException("This bundle has no video track.");

    /// <summary>Packetises and sends one encoded video frame on a simulcast <paramref name="rid"/> layer (RFC 8853).</summary>
    /// <exception cref="InvalidOperationException">This bundle has no video track.</exception>
    /// <exception cref="ArgumentException">No encoding is configured for <paramref name="rid"/>.</exception>
    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _video is { } video
            ? video.SendFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken)
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
