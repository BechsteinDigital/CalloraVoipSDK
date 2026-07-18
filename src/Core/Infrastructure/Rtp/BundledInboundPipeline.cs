using System.Net;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The inbound datagram-processing sublayer of the bundled transport (ADR-011 B2c-in-3, RFC 8843):
/// every datagram arriving on the shared 5-tuple is classified (RFC 7983 via
/// <see cref="MediaPacketClassifier"/>), then routed — STUN and DTLS out to the shared ICE/DTLS-SRTP
/// layer, RTCP through the shared inbound SRTCP context, and RTP through the shared inbound SRTP
/// context (per-SSRC state, ADR-011 B2c-in-1) before the <see cref="BundledTrackRouter"/> dispatches
/// it to the owning m-line's track sink (ADR-010 B2b).
///
/// Like <see cref="BundledTrackRouter"/> it owns no socket: the shared UDP receive loop that feeds it
/// is assembled in a later slice (B3). BUNDLE media is DTLS-SRTP only, so this pipeline fails closed —
/// RTP and RTCP are dropped until <see cref="InstallInboundKeys"/> supplies the negotiated contexts.
/// Thread-safe: the installed contexts are read through <see cref="Volatile"/> and are themselves
/// internally synchronised; the drop counter uses <see cref="Interlocked"/>.
/// </summary>
internal sealed class BundledInboundPipeline
{
    private readonly BundledTrackRouter _router;
    private readonly IRtpPacketCodec _rtpCodec;
    private readonly ILogger<BundledInboundPipeline> _logger;

    private ISrtpContext? _inboundSrtp;
    private ISrtcpContext? _inboundSrtcp;
    private long _droppedDatagrams;
    private long _rtpPacketsReceived;
    private long _rtpBytesReceived;

    /// <summary>Raised with an independent copy of a STUN datagram and its source for the ICE layer.</summary>
    public event Action<byte[], IPEndPoint>? StunPacketReceived;

    /// <summary>Raised with an independent copy of a DTLS record and its source for the handshake layer.</summary>
    public event Action<byte[], IPEndPoint>? DtlsPacketReceived;

    /// <summary>Raised with a decrypted (or plain, pre-keying this never fires) RTCP compound packet.</summary>
    public event Action<byte[]>? ControlPacketReceived;

    public BundledInboundPipeline(
        BundledTrackRouter router,
        IRtpPacketCodec rtpCodec,
        ILogger<BundledInboundPipeline> logger)
    {
        _router   = router   ?? throw new ArgumentNullException(nameof(router));
        _rtpCodec = rtpCodec ?? throw new ArgumentNullException(nameof(rtpCodec));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Datagrams dropped by this pipeline: RTP/RTCP received before keying (fail-closed), packets
    /// failing SRTP/SRTCP authentication or replay, and malformed RTP that cannot be parsed. Routing
    /// drops (an RTP packet with no matching track) are counted separately on the
    /// <see cref="BundledTrackRouter.DroppedPackets"/>.
    /// </summary>
    public long DroppedDatagrams => Interlocked.Read(ref _droppedDatagrams);

    /// <summary>Total RTP packets received and successfully SRTP-decrypted (before track routing).</summary>
    public long RtpPacketsReceived => Interlocked.Read(ref _rtpPacketsReceived);

    /// <summary>Total bytes of decrypted inbound RTP packets.</summary>
    public long RtpBytesReceived => Interlocked.Read(ref _rtpBytesReceived);

    /// <summary>
    /// Installs the shared inbound SRTP and SRTCP contexts once the DTLS-SRTP handshake has derived
    /// the keys. Until then RTP and RTCP are dropped. Both contexts are keyed from the one shared
    /// master secret and serve every SSRC the transport carries.
    /// </summary>
    public void InstallInboundKeys(ISrtpContext srtp, ISrtcpContext srtcp)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        ArgumentNullException.ThrowIfNull(srtcp);
        Volatile.Write(ref _inboundSrtp, srtp);
        Volatile.Write(ref _inboundSrtcp, srtcp);
    }

    /// <summary>
    /// Classifies and routes one inbound datagram. STUN/DTLS are handed to the shared ICE/DTLS layer
    /// via the events (only when a <paramref name="source"/> endpoint is present); RTCP and RTP are
    /// decrypted with the shared contexts and dispatched. Never throws for a malformed or unexpected
    /// datagram — it is dropped and counted so a hostile packet cannot kill the receive loop.
    /// </summary>
    public void ProcessInboundDatagram(ReadOnlySpan<byte> datagram, IPEndPoint? source)
    {
        var kind = MediaPacketClassifier.Classify(datagram);

        if (source is not null && kind is MediaPacketKind.Stun)
        {
            DispatchToEndpointHandler(StunPacketReceived, datagram, source, "STUN");
            return;
        }

        if (source is not null && kind is MediaPacketKind.Dtls)
        {
            DispatchToEndpointHandler(DtlsPacketReceived, datagram, source, "DTLS");
            return;
        }

        if (kind is MediaPacketKind.Rtcp)
        {
            ProcessRtcp(datagram, source);
            return;
        }

        ProcessRtp(datagram, source);
    }

    private void DispatchToEndpointHandler(
        Action<byte[], IPEndPoint>? handler, ReadOnlySpan<byte> datagram, IPEndPoint source, string kind)
    {
        // The receive buffer is reused for the next datagram; the ICE/DTLS handler may authenticate
        // or respond asynchronously on its own thread, so hand it an independent copy.
        var copy = datagram.ToArray();
        try
        {
            handler?.Invoke(copy, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Kind} datagram handler.", kind);
        }
    }

    private void ProcessRtcp(ReadOnlySpan<byte> datagram, IPEndPoint? source)
    {
        // Fail closed: a BUNDLE transport is DTLS-SRTP only and must never interpret RTCP before the
        // shared SRTCP context is installed.
        if (Volatile.Read(ref _inboundSrtcp) is not { } inboundSrtcp)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping inbound RTCP from {Source}: no SRTCP context installed yet.", source);
            return;
        }

        byte[] rtcp;
        try
        {
            rtcp = inboundSrtcp.UnprotectRtcp(datagram);
        }
        catch (SrtpAuthenticationException)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping SRTCP packet failing authentication from {Source}.", source);
            return;
        }
        catch (SrtpReplayException)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping replayed SRTCP packet from {Source}.", source);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)
        {
            // A too-short or otherwise malformed RTCP-looking datagram must be a clean drop — an
            // uncaught throw here would terminate the shared receive loop (DoS across all tracks).
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping malformed SRTCP packet from {Source}: {Message}", source, ex.Message);
            return;
        }

        try
        {
            ControlPacketReceived?.Invoke(rtcp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in RTP control datagram handler.");
        }
    }

    private void ProcessRtp(ReadOnlySpan<byte> datagram, IPEndPoint? source)
    {
        // Fail closed: never accept plaintext RTP on a DTLS-SRTP transport.
        if (Volatile.Read(ref _inboundSrtp) is not { } inboundSrtp)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping inbound RTP from {Source}: no SRTP context installed yet.", source);
            return;
        }

        byte[] plain;
        try
        {
            plain = inboundSrtp.Unprotect(datagram);
        }
        catch (SrtpAuthenticationException)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping SRTP packet failing authentication from {Source}.", source);
            return;
        }
        catch (SrtpReplayException)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping replayed SRTP packet from {Source}.", source);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping malformed SRTP packet from {Source}: {Message}", source, ex.Message);
            return;
        }

        Interlocked.Increment(ref _rtpPacketsReceived);
        Interlocked.Add(ref _rtpBytesReceived, plain.Length);
        RtpPacketReceived(plain, source);
    }

    private void RtpPacketReceived(byte[] plainRtp, IPEndPoint? source)
    {
        Packets.RtpPacket packet;
        try
        {
            packet = _rtpCodec.Decode(plainRtp);
        }
        catch (FormatException ex)
        {
            // Authenticated but unparseable — drop rather than let it escape the receive loop.
            Interlocked.Increment(ref _droppedDatagrams);
            _logger.LogDebug("Dropping undecodable RTP packet from {Source}: {Message}", source, ex.Message);
            return;
        }

        // The router resolves the packet's MID (SSRC latch / MID header ext / payload type) and hands
        // it to the owning track's sink, or drops+counts it on its own DroppedPackets counter.
        _router.DispatchInboundRtp(packet);
    }
}
