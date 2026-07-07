using System.Net;
using System.Net.Sockets;
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
    private readonly IRtpPacketCodec _codec;
    private readonly ILogger<RtpSession> _logger;
    private readonly object _sendSync = new();

    private readonly UdpClient _udp;
    private readonly uint _ssrc;
    private readonly Dictionary<uint, RtpSequenceValidator> _validators = new();

    // SRTP contexts for this leg (RFC 3711). Null when SRTP was not negotiated, in which
    // case RTP is exchanged as cleartext exactly as before. Outbound protects sent packets,
    // inbound unprotects received packets. SRTP applies to the media (RTP) path only.
    private readonly ISrtpContext? _outboundSrtp;
    private readonly ISrtpContext? _inboundSrtp;

    // SRTCP contexts for this leg (RFC 3711 §3.4). Null when SRTP was not negotiated, in
    // which case RTCP (the RTCP-MUX control path) stays cleartext. Outbound protects sent
    // control datagrams, inbound unprotects received control datagrams.
    private readonly ISrtcpContext? _outboundSrtcp;
    private readonly ISrtcpContext? _inboundSrtcp;

    private ushort _sequenceNumber;
    private uint _timestamp;
    private Task? _receiveLoop;
    private long _packetsSent;
    private long _octetsSent;
    private int _lastSentTimestamp;
    private int _hasSentPackets;

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

    public RtpSession(RtpSessionOptions options, IRtpPacketCodec codec, ILogger<RtpSession> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _codec   = codec;
        _logger  = logger;
        _ssrc    = options.Ssrc ?? (uint)Random.Shared.Next();
        _outboundSrtp = options.OutboundSrtp;
        _inboundSrtp  = options.InboundSrtp;
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
        _receiveLoop = RunReceiveLoopAsync(cancellationToken);
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
    /// Used by RFC 4733 telephone-event packets that must keep a constant event timestamp.
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
        // SRTCP protect (RFC 3711 §3.4): encrypt the RTCP payload and append the E||index
        // word plus the auth tag. When SRTP was not negotiated (_outboundSrtcp == null) the
        // datagram is sent as cleartext RTCP. SrtcpContext is thread-safe.
        if (_outboundSrtcp is not null)
            datagram = _outboundSrtcp.Protect(datagram.Span);

        await _udp.SendAsync(datagram, _options.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
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

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                ProcessDatagram(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "RTP socket error on {LocalEndPoint}", _options.LocalEndPoint);
            }
        }

        _logger.LogDebug("RTP receive loop stopped on {LocalEndPoint}", _options.LocalEndPoint);
    }

    private void ProcessDatagram(byte[] datagram)
    {
        if (LooksLikeRtcpDatagram(datagram))
        {
            // RTCP is routed on its unencrypted header even under SRTP (RFC 5761): the 8-byte
            // SRTCP header stays cleartext, so this check still works. SRTCP unprotect
            // (RFC 3711 §3.4) authenticates and decrypts before dispatch. A failed auth tag or
            // a replayed control packet is dropped silently while the receive loop keeps running.
            if (_inboundSrtcp is not null)
            {
                try
                {
                    datagram = _inboundSrtcp.Unprotect(datagram);
                }
                catch (SrtpAuthenticationException ex)
                {
                    _logger.LogDebug("Dropping inbound SRTCP packet with invalid authentication tag: {Message}", ex.Message);
                    return;
                }
                catch (SrtpReplayException ex)
                {
                    _logger.LogDebug("Dropping replayed inbound SRTCP packet: {Message}", ex.Message);
                    return;
                }
            }

            try
            {
                ControlPacketReceived?.Invoke(datagram);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in RTP control datagram handler.");
            }
            return;
        }

        // SRTP unprotect (RFC 3711): authenticate and decrypt before decoding. A failed auth
        // tag or a replayed packet is dropped silently (packet discarded, receive loop keeps
        // running) rather than surfaced as an error, per RFC 3711 §3.3.
        if (_inboundSrtp is not null)
        {
            try
            {
                datagram = _inboundSrtp.Unprotect(datagram);
            }
            catch (SrtpAuthenticationException ex)
            {
                _logger.LogDebug("Dropping inbound SRTP packet with invalid authentication tag: {Message}", ex.Message);
                return;
            }
            catch (SrtpReplayException ex)
            {
                _logger.LogDebug("Dropping replayed inbound SRTP packet: {Message}", ex.Message);
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

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        _udp.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Release cached SRTP/SRTCP crypto resources (AES transforms) once the receive loop
        // is guaranteed to be stopped, so Unprotect can no longer touch a disposed context.
        (_outboundSrtp as IDisposable)?.Dispose();
        (_inboundSrtp as IDisposable)?.Dispose();
        (_outboundSrtcp as IDisposable)?.Dispose();
        (_inboundSrtcp as IDisposable)?.Dispose();

        ControlPacketReceived = null;
    }

    private static bool LooksLikeRtcpDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 2)
            return false;

        var version = datagram[0] >> 6;
        if (version != 2)
            return false;

        var packetType = datagram[1];
        return packetType is >= 192 and <= 223;
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
        ushort sequenceNumber;
        uint timestamp;

        lock (_sendSync)
        {
            sequenceNumber = _sequenceNumber;
            timestamp = timestampOverride ?? _timestamp;

            // Increment sequence number (wraps at 65535 per RFC 3550 §5.1).
            unchecked { _sequenceNumber++; }

            if (advanceTimestamp)
                _timestamp += (uint)_options.SamplesPerPacket;
        }

        var packet = new RtpPacket
        {
            PayloadType = payloadType,
            Marker = marker,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = _ssrc,
            Payload = payload
        };

        var datagram = _codec.Encode(packet);

        // SRTP protect (RFC 3711): encrypt the payload and append the auth tag. When SRTP
        // was not negotiated (_outboundSrtp == null) the datagram is sent as cleartext RTP.
        // SrtpContext is thread-safe and this call already runs after the _sendSync section.
        if (_outboundSrtp is not null)
            datagram = _outboundSrtp.Protect(datagram);

        await _udp.SendAsync(datagram, _options.RemoteEndPoint, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _packetsSent);
        // Octet count intentionally tracks RTP payload length (RFC 3550 SR semantics),
        // not the SRTP auth-tag overhead added by Protect above.
        Interlocked.Add(ref _octetsSent, payload.Length);
        Volatile.Write(ref _lastSentTimestamp, unchecked((int)timestamp));
        Volatile.Write(ref _hasSentPackets, 1);
    }
}
