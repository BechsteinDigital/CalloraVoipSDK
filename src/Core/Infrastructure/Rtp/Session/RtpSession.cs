using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// UDP-based RTP session (RFC 3550).
/// Manages one bidirectional media stream: binds a UDP socket to the local endpoint,
/// sends encoded frames with auto-incremented sequence numbers and timestamps,
/// and dispatches inbound packets via <see cref="PacketReceived"/>.
/// </summary>
internal sealed class RtpSession : IRtpSession
{
    private readonly RtpSessionOptions _options;
    private readonly RtpOutboundHeaderExtensionStamper _extensionStamper;
    private readonly IRtpPacketCodec _codec;
    private readonly ILogger<RtpSession> _logger;
    private readonly object _sendSync = new();

    private readonly UdpClient _udp;
    private readonly uint _ssrc;
    private readonly Dictionary<uint, RtpSequenceValidator> _validators = new();

    // Symmetric RTP / comedia (RFC dodging NAT without ICE): once a valid RTP packet
    // arrives, remember its actual source and send back there instead of the SDP-advertised
    // address. Lets media flow through NAT without STUN — the peer's SBC latches likewise.
    private IPEndPoint? _latchedRemoteEndPoint;

    // Serializes SRTP protection: the context derives the rollover counter from the
    // packet sequence, so out-of-order protection of concurrent sends would corrupt it.
    private readonly object _srtpProtectSync = new();

    // Security contexts. Fixed from options for SDES/plain calls; the DTLS-SRTP path
    // installs them once after the handshake via InstallSecurityContexts. Written once
    // by the handshake thread, read per packet by the receive loop and senders —
    // reference reads/writes are atomic, Volatile ensures visibility.
    private ISrtpContext? _outboundSrtp;
    private ISrtpContext? _inboundSrtp;
    private ISrtcpContext? _outboundSrtcp;
    private ISrtcpContext? _inboundSrtcp;

    // Secondary multiplexed stream (RFC 4588 RTX): one additional payload type carried on the
    // same socket with its own SRTP contexts, so its independent sequence space never shares
    // the primary stream's replay window / ROC. -1 until ConfigureSecondaryStream is called;
    // contexts installed later (post-DTLS or from SDES keys) like the primary ones. Written
    // once before the receive loop starts, read on the loop thread — volatile for visibility.
    private volatile int _secondaryPayloadType = -1;
    private ISrtpContext? _secondaryOutboundSrtp;
    private ISrtpContext? _secondaryInboundSrtp;
    private readonly object _secondarySrtpProtectSync = new();

    private ushort _sequenceNumber;
    private ushort _transportCcSequence;
    private uint _timestamp;
    private Task? _receiveLoop;
    private CancellationTokenSource? _loopCts;
    private long _packetsSent;
    private long _octetsSent;
    private int _lastSentTimestamp;
    private int _hasSentPackets;

    // Set once ICE consent is lost (RFC 7675 §5.1): media/RTCP transmission ceases while the socket
    // stays open (the receive loop and STUN send path keep working for a possible ICE restart).
    private int _transmissionStopped;

    private const int ReceiveBufferSize = 8192;

    /// <inheritdoc />
    public event EventHandler<RtpPacket>? PacketReceived;

    /// <inheritdoc />
    public event EventHandler? SsrcCollisionDetected;

    /// <summary>
    /// Raised when an inbound datagram on the RTP socket is identified as RTCP
    /// in RTCP-MUX mode (RFC 5761).
    /// </summary>
    internal event Action<byte[]>? ControlPacketReceived;

    /// <summary>
    /// Raised when an inbound datagram on the media socket is classified as STUN
    /// (RFC 7983 / RFC 5764 §5.1.2 demux: first byte 0–3 plus the STUN magic cookie).
    /// Carries an independent copy of the datagram and the sender's transport address so the
    /// ICE layer can authenticate the connectivity check and send a response on this same
    /// socket (RFC 8445 §7.3). STUN datagrams are not passed to the RTP/RTCP paths.
    /// </summary>
    internal event Action<byte[], IPEndPoint>? StunPacketReceived;

    /// <summary>
    /// Raised when an inbound datagram on the media socket is classified as DTLS
    /// (RFC 5764 §5.1.2 / RFC 7983 demux: first byte 20–63). Carries an independent copy
    /// of the datagram and the sender's transport address; the DTLS-SRTP handshake layer
    /// consumes these records and answers via <see cref="SendRawAsync"/> on this same
    /// socket. DTLS datagrams are not passed to the RTP/RTCP paths.
    /// </summary>
    internal event Action<byte[], IPEndPoint>? DtlsPacketReceived;

    /// <summary>
    /// Raised for an inbound RTP packet whose payload type matches the configured secondary
    /// stream (RFC 4588 RTX). It is decrypted with the secondary SRTP context and dispatched
    /// here, never through <see cref="PacketReceived"/>, so the primary stream's replay
    /// window is untouched.
    /// </summary>
    internal event Action<RtpPacket>? SecondaryPacketReceived;

    /// <summary>
    /// Raised after each primary-stream RTP packet is successfully sent, carrying the packet
    /// that went out. Lets a retransmit buffer (RFC 4588 RTX) retain it verbatim for a later
    /// NACK-driven resend. Not raised for RTX resends (<see cref="SendSecondaryAsync"/>).
    /// </summary>
    internal event Action<RtpPacket>? PacketSent;

    public RtpSession(RtpSessionOptions options, IRtpPacketCodec codec, ILogger<RtpSession> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _extensionStamper = new RtpOutboundHeaderExtensionStamper(
            options.TransportWideCcExtensionId, options.MidExtensionId, options.Mid);
        _codec   = codec;
        _logger  = logger;
        _ssrc    = options.Ssrc ?? (uint)Random.Shared.Next();

        _outboundSrtp  = options.OutboundSrtp;
        _inboundSrtp   = options.InboundSrtp;
        _outboundSrtcp = options.OutboundSrtcp;
        _inboundSrtcp  = options.InboundSrtcp;

        // Random initial sequence number and timestamp offset (RFC 3550 §5.1)
        _sequenceNumber = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp      = (uint)Random.Shared.Next();

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveBufferSize = ReceiveBufferSize;
        _udp.Client.Bind(options.LocalEndPoint);
    }

    // -------------------------------------------------------------------------
    // Start
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Link the caller token with an internal source so DisposeAsync can stop the receive
        // loop by cancellation before the socket is disposed — cancelling the pending
        // Socket.ReceiveFromAsync yields a clean OperationCanceledException, whereas disposing
        // the socket underneath a pending Memory-based receive can surface as a raw fault.
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = RunReceiveLoopAsync(_loopCts.Token);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Send
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        bool marker = false,
        byte? payloadTypeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var payloadType = payloadTypeOverride ?? _options.PayloadType;
        await SendCoreAsync(
                payload,
                marker,
                payloadType,
                timestampOverride: null,
                advanceTimestamp: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one RTP packet with an explicitly supplied timestamp and without
    /// advancing the audio timestamp cursor used for normal media frames.
    /// Used by RFC 4733 telephone-event packets that must keep a constant event timestamp,
    /// and by video frames whose packets all share one frame-level timestamp.
    /// </summary>
    internal ValueTask SendTimestampedAsync(
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint timestamp,
        CancellationToken cancellationToken = default)
        => SendCoreAsync(
            payload,
            marker,
            payloadType,
            timestampOverride: timestamp,
            advanceTimestamp: false,
            cancellationToken);

    /// <summary>
    /// Returns the next RTP timestamp that would be used for a regular media frame.
    /// </summary>
    internal uint GetCurrentTimestamp()
    {
        lock (_sendSync)
        {
            return _timestamp;
        }
    }

    /// <summary>Local synchronization source (RFC 3550 §5.1) — used as the sender SSRC of RTCP feedback.</summary>
    internal uint LocalSsrc => _ssrc;

    /// <summary>
    /// Sends one RTCP datagram via the RTP socket (RTCP-MUX mode).
    /// </summary>
    internal ValueTask SendControlAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default)
        => SendControlCoreAsync(datagram, cancellationToken);

    private async ValueTask SendControlCoreAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _transmissionStopped) != 0)
            return;

        // SRTCP (RFC 3711 §3.4): encrypt + authenticate the RTCP datagram before it leaves
        // the socket when a context is negotiated; otherwise send plain RTCP.
        if (Volatile.Read(ref _outboundSrtcp) is { } outboundSrtcp)
        {
            try
            {
                datagram = outboundSrtcp.ProtectRtcp(datagram.Span);
            }
            catch (ObjectDisposedException)
            {
                // A send racing session teardown after the context owner zeroed the keys —
                // suppress the packet; never fall through to a plain-RTCP send.
                _logger.LogDebug("Suppressing outbound RTCP: SRTCP context disposed during teardown.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            // Fail closed (DTLS-SRTP before handshake completion): never leak plain RTCP.
            _logger.LogDebug("Suppressing outbound RTCP: encrypted media required but no SRTCP context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, Volatile.Read(ref _latchedRemoteEndPoint) ?? _options.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ceases media and RTCP transmission on this session (RFC 7675 §5.1) after ICE consent is lost.
    /// Idempotent. The socket, receive loop and STUN send path stay open so a possible ICE restart
    /// can re-probe the peer.
    /// </summary>
    internal void StopTransmission() => Volatile.Write(ref _transmissionStopped, 1);

    /// <summary>
    /// Sends a raw datagram to an explicit destination on the media socket, without RTP framing
    /// or SRTP protection. Used by the ICE layer to send STUN connectivity-check responses and
    /// checks to the peer on the same 5-tuple as media (RFC 8445 §7.3). Unlike media/RTCP sends
    /// this targets the caller-supplied address, not the symmetric-RTP latch.
    /// </summary>
    internal async ValueTask SendRawAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await _udp.SendAsync(datagram, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs the SRTP/SRTCP contexts negotiated after session start (DTLS-SRTP: keys
    /// exist only once the handshake completed, RFC 5764 §4.2). Intended to be called
    /// exactly once by the DTLS attachment; together with
    /// <see cref="RtpSessionOptions.RequireEncryptedMedia"/> the session is fail-closed
    /// until this point. The caller retains ownership (disposal) of the contexts.
    /// </summary>
    internal void InstallSecurityContexts(
        ISrtpContext outboundSrtp,
        ISrtpContext inboundSrtp,
        ISrtcpContext outboundSrtcp,
        ISrtcpContext inboundSrtcp)
    {
        ArgumentNullException.ThrowIfNull(outboundSrtp);
        ArgumentNullException.ThrowIfNull(inboundSrtp);
        ArgumentNullException.ThrowIfNull(outboundSrtcp);
        ArgumentNullException.ThrowIfNull(inboundSrtcp);

        Volatile.Write(ref _outboundSrtp, outboundSrtp);
        Volatile.Write(ref _inboundSrtp, inboundSrtp);
        Volatile.Write(ref _outboundSrtcp, outboundSrtcp);
        Volatile.Write(ref _inboundSrtcp, inboundSrtcp);
    }

    /// <summary>
    /// Routes inbound RTP packets of <paramref name="payloadType"/> to
    /// <see cref="SecondaryPacketReceived"/> (RFC 4588 RTX) instead of the primary path.
    /// Call once before the receive loop dispatches secondary traffic. The caller retains
    /// ownership of the contexts installed via <see cref="InstallSecondarySecurityContexts"/>.
    /// </summary>
    internal void ConfigureSecondaryStream(byte payloadType) => _secondaryPayloadType = payloadType;

    /// <summary>The configured secondary-stream payload type, or <c>null</c> when none.</summary>
    internal byte? SecondaryPayloadType => _secondaryPayloadType >= 0 ? (byte)_secondaryPayloadType : null;

    /// <summary>
    /// Installs the SRTP contexts for the secondary (RTX) stream — separate from the primary
    /// ones so its independent sequence space has its own replay window / ROC, though the
    /// keys are the same as the primary stream's (RFC 4588 §9). See
    /// <see cref="InstallSecurityContexts"/> for the fail-closed contract.
    /// </summary>
    internal void InstallSecondarySecurityContexts(ISrtpContext outbound, ISrtpContext inbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(inbound);
        Volatile.Write(ref _secondaryOutboundSrtp, outbound);
        Volatile.Write(ref _secondaryInboundSrtp, inbound);
    }

    /// <summary>
    /// Sends a pre-built secondary-stream packet (RFC 4588 RTX) to the media peer, protected
    /// with the secondary SRTP context. Fail-closed: on an encrypted-media leg with no
    /// secondary context installed, the send is suppressed rather than leaking plaintext.
    /// The packet carries its own SSRC and sequence number (the caller's RTX stream).
    /// </summary>
    internal async ValueTask SendSecondaryAsync(RtpPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (Volatile.Read(ref _transmissionStopped) != 0)
            return;

        var datagram = _codec.Encode(packet);
        if (Volatile.Read(ref _secondaryOutboundSrtp) is { } outbound)
        {
            try
            {
                lock (_secondarySrtpProtectSync)
                    datagram = outbound.Protect(datagram);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Suppressing secondary RTP: context disposed during teardown.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            _logger.LogDebug("Suppressing secondary RTP: encrypted media required but no context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, Volatile.Read(ref _latchedRemoteEndPoint) ?? _options.RemoteEndPoint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns an immutable sender-side RTP snapshot for RTCP SR generation.
    /// </summary>
    internal RtpSenderStatisticsSnapshot GetSenderStatisticsSnapshot()
    {
        var packetsSent = Interlocked.Read(ref _packetsSent);
        var octetsSent = Interlocked.Read(ref _octetsSent);
        return new RtpSenderStatisticsSnapshot(
            LocalSsrc: _ssrc,
            SenderPacketCount: ClampToUInt32(packetsSent),
            SenderOctetCount: ClampToUInt32(octetsSent),
            LastSentRtpTimestamp: unchecked((uint)Volatile.Read(ref _lastSentTimestamp)),
            HasSentPackets: Volatile.Read(ref _hasSentPackets) != 0);
    }

    // -------------------------------------------------------------------------
    // Receive loop
    // -------------------------------------------------------------------------

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("RTP receive loop started on {LocalEndPoint}", _options.LocalEndPoint);

        // One pooled receive buffer for the whole loop. The loop is single-threaded and
        // ProcessDatagram copies every byte it retains (the codec copies the payload, SRTP
        // returns a fresh array, the RTCP path clones before dispatch) before the next
        // receive overwrites the buffer — so a single reused buffer is safe and removes the
        // per-datagram byte[] that UdpClient.ReceiveAsync allocated on every packet.
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var remoteTemplate = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.Client
                        .ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken)
                        .ConfigureAwait(false);
                    ProcessDatagram(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break; // Socket disposed during shutdown.
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    // Torn down during shutdown. Windows surfaces the socket close as a
                    // WSAECONNRESET ("connection forcibly closed") on the pending receive after a
                    // prior send hit an ICMP port-unreachable; that must not fault the loop and
                    // propagate out of DisposeAsync. Benign — stop receiving.
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "RTP socket error on {LocalEndPoint}", _options.LocalEndPoint);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _logger.LogDebug("RTP receive loop stopped on {LocalEndPoint}", _options.LocalEndPoint);
    }

    private void ProcessDatagram(ReadOnlySpan<byte> datagram, IPEndPoint? source)
    {
        // RFC 7983 demux (STUN/DTLS/RTP/RTCP share the media 5-tuple): classify once, then route.
        var kind = MediaPacketClassifier.Classify(datagram);

        // STUN connectivity checks — routed out before any RTP/RTCP interpretation; the ICE layer
        // owns the response.
        if (source is not null && kind is MediaPacketKind.Stun)
        {
            // The receive buffer is reused for the next datagram; the ICE handler may
            // authenticate or respond asynchronously, so hand it an independent copy.
            var stunDatagram = datagram.ToArray();
            try
            {
                StunPacketReceived?.Invoke(stunDatagram, source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in STUN datagram handler.");
            }
            return;
        }

        // DTLS records (RFC 5764 §5.1.2 / RFC 7983) — routed to the DTLS-SRTP handshake layer.
        if (source is not null && kind is MediaPacketKind.Dtls)
        {
            // Independent copy — the receive buffer is reused and the handshake engine
            // consumes the record on its own thread.
            var dtlsDatagram = datagram.ToArray();
            try
            {
                DtlsPacketReceived?.Invoke(dtlsDatagram, source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in DTLS datagram handler.");
            }
            return;
        }

        if (kind is MediaPacketKind.Rtcp)
        {
            // SRTCP (RFC 3711 §3.4): authenticate + decrypt before dispatch when a context is
            // negotiated. UnprotectRtcp returns a fresh array; on plain RTCP we copy, since the
            // receive buffer is reused and RTCP handlers may parse/queue asynchronously.
            byte[] rtcpDatagram;
            if (_options.RequireEncryptedMedia && Volatile.Read(ref _inboundSrtcp) is null)
            {
                // Fail closed (DTLS-SRTP before handshake completion): a keyed call must
                // never interpret unauthenticated RTCP.
                _logger.LogDebug("Dropping inbound RTCP from {Source}: encrypted media required but no SRTCP context installed yet.", source);
                return;
            }

            if (Volatile.Read(ref _inboundSrtcp) is { } inboundSrtcp)
            {
                try
                {
                    rtcpDatagram = inboundSrtcp.UnprotectRtcp(datagram);
                }
                catch (SrtpAuthenticationException)
                {
                    _logger.LogDebug("Dropping SRTCP packet failing authentication from {Source}.", source);
                    return;
                }
                catch (SrtpReplayException)
                {
                    _logger.LogDebug("Dropping replayed SRTCP packet from {Source}.", source);
                    return;
                }
                catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)
                {
                    // A too-short or otherwise malformed RTCP-looking datagram (it passed the
                    // version/PT demux but not the SRTCP length/parse) must be a clean drop —
                    // an uncaught throw here would terminate the whole receive loop (DoS).
                    // ObjectDisposedException covers a receive racing session teardown while
                    // the context owner (DTLS attachment) already zeroed the keys.
                    _logger.LogDebug("Dropping malformed SRTCP packet from {Source}: {Message}", source, ex.Message);
                    return;
                }
            }
            else
            {
                rtcpDatagram = datagram.ToArray();
            }

            try
            {
                ControlPacketReceived?.Invoke(rtcpDatagram);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in RTP control datagram handler.");
            }
            return;
        }

        // Secondary stream (RFC 4588 RTX): a configured payload type is decrypted with its
        // own SRTP context and dispatched apart, so its independent sequence space does not
        // disturb the primary stream's replay window. The RTP header (incl. PT, byte 1 low
        // 7 bits) is plaintext under SRTP, so the routing decision is safe pre-decrypt.
        if (_secondaryPayloadType >= 0
            && datagram.Length >= 2
            && (datagram[1] & 0x7F) == _secondaryPayloadType)
        {
            ProcessSecondaryDatagram(datagram, source);
            return;
        }

        // SRTP (RFC 3711): authenticate and decrypt before any RTP interpretation.
        // A packet failing the auth tag or replay check is dropped here — it never
        // reaches the codec, the jitter buffer, or the symmetric-RTP latch.
        if (_options.RequireEncryptedMedia && Volatile.Read(ref _inboundSrtp) is null)
        {
            // Fail closed (DTLS-SRTP before handshake completion): a keyed call must never
            // accept plaintext RTP — it would also poison the symmetric-RTP latch.
            _logger.LogDebug("Dropping inbound RTP from {Source}: encrypted media required but no SRTP context installed yet.", source);
            return;
        }

        if (Volatile.Read(ref _inboundSrtp) is { } inboundSrtp)
        {
            try
            {
                datagram = inboundSrtp.Unprotect(datagram);
            }
            catch (SrtpAuthenticationException)
            {
                _logger.LogDebug("Dropping SRTP packet failing authentication from {Source}.", source);
                return;
            }
            catch (SrtpReplayException)
            {
                _logger.LogDebug("Dropping replayed SRTP packet from {Source}.", source);
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)
            {
                // A too-short or malformed RTP-looking datagram (it passed the STUN/RTCP demux
                // but is shorter than 12 + auth-tag, or has a malformed header) must be a clean
                // drop — an uncaught throw here would terminate the whole receive loop (DoS).
                // ObjectDisposedException covers a receive racing session teardown while the
                // context owner (DTLS attachment) already zeroed the keys.
                _logger.LogDebug("Dropping undecryptable SRTP packet from {Source}: {Message}", source, ex.Message);
                return;
            }
        }

        RtpPacket packet;
        try
        {
            packet = _codec.Decode(datagram);
        }
        catch (FormatException ex)
        {
            _logger.LogDebug("Dropping malformed RTP datagram: {Message}", ex.Message);
            return;
        }

        // Symmetric RTP: latch onto the real source of the first valid RTP packet so
        // outbound media follows the NAT-translated path the peer actually uses.
        if (source is not null && !source.Equals(Volatile.Read(ref _latchedRemoteEndPoint)))
        {
            Volatile.Write(ref _latchedRemoteEndPoint, source);
            _logger.LogDebug("RTP symmetric latch: sending media to observed source {Source}.", source);
        }

        // SSRC collision detection (RFC 3550 §8.2)
        if (packet.Ssrc == _ssrc)
        {
            _logger.LogWarning("SSRC collision detected (SSRC={Ssrc:X8})", _ssrc);
            try { SsrcCollisionDetected?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled exception in SsrcCollisionDetected handler"); }
            return;
        }

        // Sequence number validation (RFC 3550 §A.1)
        if (!_validators.TryGetValue(packet.Ssrc, out var validator))
        {
            validator = new RtpSequenceValidator();
            _validators[packet.Ssrc] = validator;
        }

        var result = validator.Validate(packet.SequenceNumber);
        switch (result)
        {
            case RtpSequenceResult.Valid:
                break;
            case RtpSequenceResult.Probation:
                _logger.LogDebug("RTP SSRC={Ssrc:X8} on probation, seq={Seq}", packet.Ssrc, packet.SequenceNumber);
                return;
            case RtpSequenceResult.Duplicate:
                _logger.LogDebug("RTP duplicate dropped: SSRC={Ssrc:X8} seq={Seq}", packet.Ssrc, packet.SequenceNumber);
                return;
            case RtpSequenceResult.TooLate:
                _logger.LogDebug(
                    "RTP out-of-order packet forwarded to jitter buffer: SSRC={Ssrc:X8} seq={Seq}",
                    packet.Ssrc,
                    packet.SequenceNumber);
                break;
            case RtpSequenceResult.SequenceJump:
                _logger.LogWarning("RTP sequence jump detected: SSRC={Ssrc:X8} seq={Seq} — source may have restarted", packet.Ssrc, packet.SequenceNumber);
                return;
        }

        try
        {
            PacketReceived?.Invoke(this, packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in RTP PacketReceived handler");
        }
    }

    // Decrypts a secondary-stream datagram with its own SRTP context and dispatches it,
    // mirroring the primary path's fail-closed drops (auth/replay/malformed never kill the
    // receive loop). Deliberately skips the symmetric-RTP latch and SSRC validation: the
    // secondary stream (RTX) rides the already-latched media 5-tuple and its own sequence
    // space is validated by the consumer via the recovered original packet.
    private void ProcessSecondaryDatagram(ReadOnlySpan<byte> datagram, IPEndPoint? source)
    {
        if (_options.RequireEncryptedMedia && Volatile.Read(ref _secondaryInboundSrtp) is null)
        {
            _logger.LogDebug("Dropping secondary RTP from {Source}: encrypted media required but no context installed yet.", source);
            return;
        }

        if (Volatile.Read(ref _secondaryInboundSrtp) is { } inbound)
        {
            try
            {
                datagram = inbound.Unprotect(datagram);
            }
            catch (SrtpAuthenticationException)
            {
                _logger.LogDebug("Dropping secondary SRTP packet failing authentication from {Source}.", source);
                return;
            }
            catch (SrtpReplayException)
            {
                _logger.LogDebug("Dropping replayed secondary SRTP packet from {Source}.", source);
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException)
            {
                _logger.LogDebug("Dropping undecryptable secondary SRTP packet from {Source}: {Message}", source, ex.Message);
                return;
            }
        }

        RtpPacket packet;
        try
        {
            packet = _codec.Decode(datagram);
        }
        catch (FormatException ex)
        {
            _logger.LogDebug("Dropping malformed secondary RTP datagram: {Message}", ex.Message);
            return;
        }

        try
        {
            SecondaryPacketReceived?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in secondary RTP handler.");
        }
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        // Stop the receive loop by cancellation first, then dispose the socket only after the
        // loop has drained — avoids disposing the socket underneath a pending receive.
        _loopCts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _loopCts?.Dispose();
        _udp.Dispose();
        ControlPacketReceived = null;
        StunPacketReceived = null;
        DtlsPacketReceived = null;
        SecondaryPacketReceived = null;
        PacketSent = null;
    }

    private static uint ClampToUInt32(long value)
    {
        if (value <= 0)
            return 0;

        if (value >= uint.MaxValue)
            return uint.MaxValue;

        return (uint)value;
    }

    private async ValueTask SendCoreAsync(
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint? timestampOverride,
        bool advanceTimestamp,
        CancellationToken cancellationToken)
    {
        // RFC 7675 §5.1: once ICE consent is lost, stop transmitting media on this pair.
        if (Volatile.Read(ref _transmissionStopped) != 0)
            return;

        ushort sequenceNumber;
        uint timestamp;
        ushort? transportCcSequence = null;

        lock (_sendSync)
        {
            sequenceNumber = _sequenceNumber;
            timestamp = timestampOverride ?? _timestamp;

            // Increment sequence number (wraps at 65535 per RFC 3550 §5.1).
            unchecked { _sequenceNumber++; }

            if (advanceTimestamp)
                _timestamp += (uint)_options.SamplesPerPacket;

            // Transport-wide sequence number (transport-cc / RFC 8888): a monotonic counter across
            // this transport's primary packets, allocated under the same lock so it stays ordered.
            if (_options.TransportWideCcExtensionId is not null)
            {
                transportCcSequence = _transportCcSequence;
                unchecked { _transportCcSequence++; }
            }
        }

        var packet = new RtpPacket
        {
            PayloadType = payloadType,
            Marker = marker,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = _ssrc,
            Payload = payload,
            // Stamp the header extension (transport-cc, and MID on a BUNDLE transport) before SRTP:
            // RFC 3711 authenticates but does not encrypt the header extension, so the receiver reads
            // the counter and MID in the clear. When MID is not negotiated the bytes are identical to
            // stamping transport-cc alone. FOLLOW-UP (perf): still ~2 heap objects per stamped packet;
            // full pooling — reusing them across packets — remains open.
            HeaderExtension = _extensionStamper.Build(transportCcSequence)
        };

        var datagram = _codec.Encode(packet);

        // SRTP (RFC 3711): protect the full RTP packet with our negotiated key. The
        // context tracks the rollover counter from sequence numbers, so concurrent
        // sends must serialize protection.
        if (Volatile.Read(ref _outboundSrtp) is { } outboundSrtp)
        {
            try
            {
                lock (_srtpProtectSync)
                    datagram = outboundSrtp.Protect(datagram);
            }
            catch (ObjectDisposedException)
            {
                // A send racing session teardown after the context owner zeroed the keys —
                // suppress the packet; never fall through to an unprotected send.
                _logger.LogDebug("Suppressing outbound RTP: SRTP context disposed during teardown.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            // Fail closed (DTLS-SRTP before handshake completion): never leak plain media.
            _logger.LogDebug("Suppressing outbound RTP: encrypted media required but no SRTP context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, Volatile.Read(ref _latchedRemoteEndPoint) ?? _options.RemoteEndPoint, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _packetsSent);
        Interlocked.Add(ref _octetsSent, payload.Length);
        Volatile.Write(ref _lastSentTimestamp, unchecked((int)timestamp));
        Volatile.Write(ref _hasSentPackets, 1);

        // Notify after a successful send so a retransmit buffer (RFC 4588 RTX) can retain the
        // exact packet that went out. Fired for primary-stream sends only, not RTX resends.
        try
        {
            PacketSent?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in RTP PacketSent handler.");
        }
    }
}
