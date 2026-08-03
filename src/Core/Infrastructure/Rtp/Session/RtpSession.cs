using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
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

    // Our synchronisation source (RFC 3550 §5). Mutable: on an SSRC collision (§8.2) we send a BYE for the
    // old value and adopt a fresh one. Read on the receive loop, senders, and the SR snapshot — accessed via
    // Volatile / under _sendSync so the swap publishes atomically with the re-seeded sequence + timestamp.
    private uint _ssrc;

    // Stateless RTCP wire codec used to build the collision BYE (RFC 3550 §6.6). Direct instantiation matches
    // the established pattern for these leaf wire codecs (e.g. BundledMediaSession's RtcpPacketCodec).
    private readonly IRtcpPacketCodec _rtcpCodec = new RtcpPacketCodec();

    // Per-SSRC RFC 3550 §A.1 sequence validators, capped + LRU-evicted (memory-DoS bound). Accessed only on the
    // single receive-loop thread. See RtpTrackedSsrcTable.
    private readonly RtpTrackedSsrcTable _ssrcTable;

    // Symmetric RTP / comedia (NAT without ICE): outbound media follows the peer's observed source instead of
    // the SDP-advertised address. The hardened re-latch policy (CVE-2017-14099) lives in SymmetricRtpLatch.
    private readonly SymmetricRtpLatch _latch;

    // Serializes SRTP protection: the context derives the rollover counter from the
    // packet sequence, so out-of-order protection of concurrent sends would corrupt it.
    private readonly object _srtpProtectSync = new();

    // Outbound security contexts. Fixed from options for SDES/plain calls; the DTLS-SRTP path installs them
    // once after the handshake via InstallSecurityContexts. Written once by the handshake thread, read per
    // packet by the senders — reference reads/writes are atomic, Volatile ensures visibility. The matching
    // inbound contexts live on _inbound (RtpInboundProcessor), which owns the receive pipeline.
    private ISrtpContext? _outboundSrtp;
    private ISrtcpContext? _outboundSrtcp;

    // Secondary multiplexed stream (RFC 4588 RTX): one additional payload type carried on the same socket with
    // its own SRTP contexts, so its independent sequence space never shares the primary stream's replay window
    // / ROC. The outbound context and its send serialization live here; the inbound context and the payload-type
    // routing live on _inbound.
    private ISrtpContext? _secondaryOutboundSrtp;
    private readonly object _secondarySrtpProtectSync = new();

    // Inbound half: demux, decrypt, sequence-validate and dispatch (see RtpInboundProcessor). Constructed in the
    // ctor once the collaborators (latch, ssrc table, codecs) exist; driven from the single receive loop.
    private readonly RtpInboundProcessor _inbound;

    private ushort _sequenceNumber;
    private ushort _transportCcSequence;
    private uint _timestamp;
    private Task? _receiveLoop;
    private CancellationTokenSource? _loopCts;
    private int _started;
    // Coordinates StartAsync's _loopCts/_receiveLoop writes with DisposeAsync's reads so a Start racing a Dispose
    // never orphans the loop, and a Start after disposal does not spin up a loop on the disposed socket.
    private readonly object _lifecycleSync = new();
    private bool _disposed;
    private long _packetsSent;
    private long _octetsSent;
    private int _lastSentTimestamp;
    private int _hasSentPackets;

    // Set once ICE consent is lost (RFC 7675 §5.1): media/RTCP transmission ceases while the socket
    // stays open (the receive loop and STUN send path keep working for a possible ICE restart).
    private int _transmissionStopped;

    /// <inheritdoc />
    public event EventHandler<RtpPacket>? PacketReceived;

    /// <inheritdoc />
    public event EventHandler? SsrcCollisionDetected;

    /// <summary>
    /// Raised when an inbound datagram on the RTP socket is identified as RTCP in RTCP-MUX mode (RFC 5761),
    /// carrying the decoded compound. The compound is decoded once here and the shared, read-only list is
    /// handed to every subscriber (quality monitor, keyframe feedback, transport-cc) so the same bytes are not
    /// re-parsed per consumer.
    /// </summary>
    internal event Action<IReadOnlyList<RtcpPacket>>? RtcpCompoundReceived;

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

    /// <param name="preBoundSocket">
    /// A socket already bound to the media port (handed over from <c>MediaPortReservation</c>). When
    /// supplied the session takes ownership and uses it as-is — no rebind, which is how the port-ownership
    /// race is avoided (reference-parity: the reserved socket <em>is</em> the media socket). When
    /// <see langword="null"/> the session binds <see cref="RtpSessionOptions.LocalEndPoint"/> itself,
    /// preserving the legacy path (e.g. the DTLS/ICE flows that own their socket elsewhere).
    /// </param>
    public RtpSession(
        RtpSessionOptions options, IRtpPacketCodec codec, ILogger<RtpSession> logger, UdpClient? preBoundSocket = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _extensionStamper = new RtpOutboundHeaderExtensionStamper(
            options.TransportWideCcExtensionId, options.MidExtensionId, options.Mid);
        _codec   = codec;
        _logger  = logger;
        _latch = new SymmetricRtpLatch(logger);
        _ssrcTable = new RtpTrackedSsrcTable(logger);
        // RFC 3550 §8.1 / security considerations: the SSRC is drawn from the full 32-bit space with a
        // cryptographically strong RNG (see RtpRandom — Random.Shared is a non-crypto PRNG and (uint)Next() never
        // sets the high bit, so it only covered 31 bits), so an off-path attacker cannot predict it.
        _ssrc    = options.Ssrc ?? RtpRandom.NextUInt32();

        _outboundSrtp  = options.OutboundSrtp;
        _outboundSrtcp = options.OutboundSrtcp;

        // The receive pipeline: owns the inbound contexts (seeded from options) and dispatches back through the
        // session's events, so subscriber changes and teardown clearing are reflected at call time and the public
        // PacketReceived keeps the session as its sender. A detected SSRC collision (§8.2) routes to the session,
        // which owns the send-side sequence/timestamp/SSRC reseed.
        _inbound = new RtpInboundProcessor(
            options, codec, _rtcpCodec, _latch, _ssrcTable, logger,
            localSsrc: () => LocalSsrc,
            onSsrcCollision: ResolveSsrcCollision,
            onPacketReceived: packet => PacketReceived?.Invoke(this, packet),
            onRtcpCompound: packets => RtcpCompoundReceived?.Invoke(packets),
            onStun: (datagram, source) => StunPacketReceived?.Invoke(datagram, source),
            onDtls: (datagram, source) => DtlsPacketReceived?.Invoke(datagram, source),
            onSecondary: packet => SecondaryPacketReceived?.Invoke(packet));

        // Random initial sequence number and timestamp offset (RFC 3550 §5.1): cryptographically strong and full
        // range (the old Random.Shared.Next(ushort.MaxValue) also never reached 65535, and Next() was 31-bit).
        _sequenceNumber = (ushort)RtpRandom.NextUInt32();
        _timestamp      = RtpRandom.NextUInt32();

        // Kernel SO_RCVBUF (queues many pending datagrams) — distinct from the per-datagram user-space
        // buffer used by the receive loop below (MediaSocketDefaults.DatagramBufferBytes).
        if (preBoundSocket is not null)
        {
            // Ownership transferred: already bound to the media port and held continuously since
            // reservation, so there is no rebind window for another call to steal the port.
            _udp = preBoundSocket;
            _udp.Client.ReceiveBufferSize = options.SocketReceiveBufferBytes;
        }
        else
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.ReceiveBufferSize = options.SocketReceiveBufferBytes;
            _udp.Client.Bind(options.LocalEndPoint);
        }
    }

    /// <summary>
    /// The kernel receive buffer (SO_RCVBUF) the OS actually granted for the media socket, in bytes —
    /// an internal diagnostic seam (the OS may clamp the requested value to its own maximum).
    /// </summary>
    internal int EffectiveSocketReceiveBufferBytes => _udp.Client.ReceiveBufferSize;

    // -------------------------------------------------------------------------
    // Start
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: a second StartAsync must not replace _loopCts/_receiveLoop and orphan the first receive
        // loop (which would then run un-cancelled until the socket is disposed) — mirrors the bundle guard (HARD-C5).
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        // Link the caller token with an internal source so DisposeAsync can stop the receive loop by cancellation
        // before the socket is disposed. Assigned under _lifecycleSync so a concurrent DisposeAsync either observes
        // the loop (and drains it) or wins first, marking _disposed so this Start does not spin up a doomed loop.
        lock (_lifecycleSync)
        {
            if (_disposed)
                return Task.CompletedTask;

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveLoop = RunReceiveLoopAsync(_loopCts.Token);
        }
        return Task.CompletedTask;
    }

    /// <summary>Test-only: the receive-loop task, to assert <see cref="StartAsync"/> idempotency (no orphaned loop).</summary>
    internal Task? ReceiveLoopForTest => _receiveLoop;

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

    /// <summary>
    /// Reserves <paramref name="units"/> of timestamp space for an out-of-band RFC 4733 telephone-event burst:
    /// returns the current cursor to stamp the burst with, then advances the cursor past it so a following event
    /// or media frame carries a distinct, monotonically increasing timestamp (RFC 4733 §2.5.1.4). Without this,
    /// consecutive DTMF tones reuse the same timestamp and a receiver folds them into one, dropping the repeat.
    /// </summary>
    /// <param name="units">The burst's duration in RTP timestamp units to advance the cursor by.</param>
    internal uint ReserveTimestamp(uint units)
    {
        lock (_sendSync)
        {
            var reserved = _timestamp;
            _timestamp += units;
            return reserved;
        }
    }

    /// <summary>Local synchronization source (RFC 3550 §5.1) — used as the sender SSRC of RTCP feedback.</summary>
    internal uint LocalSsrc => Volatile.Read(ref _ssrc);

    /// <summary>Number of distinct inbound SSRCs currently tracked (test/diagnostic seam).</summary>
    internal int TrackedSsrcCount => _ssrcTable.Count;

    /// <summary>True when the given SSRC currently has a sequence validator (test/diagnostic seam).</summary>
    internal bool IsSsrcTracked(uint ssrc) => _ssrcTable.Contains(ssrc);

    /// <summary>
    /// Feeds one inbound datagram through the receive pipeline synchronously, bypassing the socket.
    /// Test seam only, so the SSRC-tracking cap can be exercised deterministically on one thread —
    /// never called on the runtime receive path.
    /// </summary>
    internal void InjectInboundDatagramForTest(ReadOnlySpan<byte> datagram)
        => _inbound.Process(datagram, source: null);

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
            catch (SrtpKeyLifetimeExceededException ex)
            {
                // Per-key SRTCP index budget exhausted (RFC 3711 §9.2, #157 P1-1): fail closed — no
                // reused keystream, no plain-RTCP send. RTCP goes silent until rekey.
                _logger.LogError(ex, "Suppressing outbound RTCP: SRTCP key lifetime exhausted; media requires rekey.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            // Fail closed (DTLS-SRTP before handshake completion): never leak plain RTCP.
            _logger.LogDebug("Suppressing outbound RTCP: encrypted media required but no SRTCP context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, _latch.Target(_options.RemoteEndPoint), cancellationToken).ConfigureAwait(false);
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
        Volatile.Write(ref _outboundSrtcp, outboundSrtcp);
        _inbound.InstallInbound(inboundSrtp, inboundSrtcp);
    }

    /// <summary>
    /// Routes inbound RTP packets of <paramref name="payloadType"/> to
    /// <see cref="SecondaryPacketReceived"/> (RFC 4588 RTX) instead of the primary path.
    /// Call once before the receive loop dispatches secondary traffic. The caller retains
    /// ownership of the contexts installed via <see cref="InstallSecondarySecurityContexts"/>.
    /// </summary>
    internal void ConfigureSecondaryStream(byte payloadType) => _inbound.ConfigureSecondaryStream(payloadType);

    /// <summary>The configured secondary-stream payload type, or <c>null</c> when none.</summary>
    internal byte? SecondaryPayloadType => _inbound.SecondaryPayloadType;

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
        _inbound.InstallSecondaryInbound(inbound);
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
            catch (SrtpKeyLifetimeExceededException ex)
            {
                // Per-key packet budget exhausted (RFC 3711 §9.2, #157 P1-1): fail closed — no reused
                // keystream, no plaintext. The secondary (RTX) leg goes silent until rekey.
                _logger.LogError(ex, "Suppressing secondary RTP: SRTP key lifetime exhausted; media requires rekey.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            _logger.LogDebug("Suppressing secondary RTP: encrypted media required but no context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, _latch.Target(_options.RemoteEndPoint), cancellationToken)
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
            LocalSsrc: Volatile.Read(ref _ssrc),
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

        // One pooled receive buffer for the whole loop. The loop is single-threaded and the inbound
        // processor copies every byte it retains (the codec copies the payload, SRTP
        // returns a fresh array, the RTCP path clones before dispatch) before the next
        // receive overwrites the buffer — so a single reused buffer is safe and removes the
        // per-datagram byte[] that UdpClient.ReceiveAsync allocated on every packet.
        var buffer = ArrayPool<byte>.Shared.Rent(MediaSocketDefaults.DatagramBufferBytes);
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
                    _inbound.Process(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
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

    // RFC 3550 §8.2: a third party is transmitting with our SSRC. Send a best-effort RTCP BYE for the
    // departing SSRC, then adopt a fresh one with a re-seeded sequence number and timestamp so our outbound
    // stream is unambiguous again. Runs on the receive loop (same thread as all _ssrcTable access); the
    // sequence/timestamp/SSRC swap takes _sendSync so a concurrent send observes a consistent triple.
    private void ResolveSsrcCollision(uint collidingSsrc)
    {
        var oldSsrc = collidingSsrc; // equals the current _ssrc at the point of detection

        uint newSsrc;
        do
        {
            newSsrc = RtpRandom.NextUInt32();
        }
        while (newSsrc == oldSsrc || _ssrcTable.Contains(newSsrc));

        lock (_sendSync)
        {
            // A new source identity restarts the sequence and timestamp offsets (RFC 3550 §5.1 / §8.2),
            // re-seeded from the same crypto-strong full-range source (RtpRandom).
            _sequenceNumber = (ushort)RtpRandom.NextUInt32();
            _timestamp = RtpRandom.NextUInt32();
            _ssrc = newSsrc;
        }

        _logger.LogWarning(
            "SSRC collision (SSRC={Old:X8}): adopting new SSRC={New:X8} and sending RTCP BYE for the old one (RFC 3550 §8.2).",
            oldSsrc, newSsrc);

        // Best-effort BYE for the departing SSRC over the fail-closed SRTCP control send, fired off the
        // receive loop so a slow or failed send never stalls inbound processing (failures are logged, not thrown).
        _ = SendCollisionByeAsync(oldSsrc);

        try
        {
            SsrcCollisionDetected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in SsrcCollisionDetected handler");
        }
    }

    // Encodes and sends an RTCP BYE announcing the departing SSRC is leaving (RFC 3550 §6.6 / §8.2), over the
    // same fail-closed SRTCP control path as SR/RR. Best-effort: any failure is logged, never propagated.
    private async Task SendCollisionByeAsync(uint departingSsrc)
    {
        try
        {
            var bye = new RtcpByePacket { Sources = new[] { departingSsrc }, Reason = "ssrc collision" };
            var datagram = _rtcpCodec.Encode(new RtcpPacket[] { bye });
            await SendControlAsync(datagram).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send RTCP BYE for departing SSRC={Ssrc:X8} after a collision.", departingSsrc);
        }
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        // Capture the loop state under _lifecycleSync (idempotent, and race-safe against a concurrent StartAsync:
        // once _disposed is set a racing Start returns without creating a loop).
        CancellationTokenSource? loopCts;
        Task? receiveLoop;
        lock (_lifecycleSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            loopCts = _loopCts;
            receiveLoop = _receiveLoop;
        }

        // Stop the receive loop by cancellation first, then dispose the socket only after the
        // loop has drained — avoids disposing the socket underneath a pending receive.
        loopCts?.Cancel();
        if (receiveLoop is not null)
        {
            try { await receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        loopCts?.Dispose();
        _udp.Dispose();
        RtcpCompoundReceived = null;
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
        uint ssrc;
        ushort? transportCcSequence = null;

        lock (_sendSync)
        {
            // Read the SSRC under the same lock the collision reseed takes, so a mid-send SSRC swap
            // (RFC 3550 §8.2) never pairs a new SSRC with the old sequence/timestamp.
            ssrc = _ssrc;
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
            Ssrc = ssrc,
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
            catch (SrtpKeyLifetimeExceededException ex)
            {
                // The key's packet budget is exhausted (RFC 3711 §9.2, #157 P1-1). Fail closed: never
                // emit a reused-keystream packet and never fall back to plaintext. The leg goes silent
                // until the session rekeys.
                _logger.LogError(ex, "Suppressing outbound RTP: SRTP key lifetime exhausted; media requires rekey.");
                return;
            }
        }
        else if (_options.RequireEncryptedMedia)
        {
            // Fail closed (DTLS-SRTP before handshake completion): never leak plain media.
            _logger.LogDebug("Suppressing outbound RTP: encrypted media required but no SRTP context installed yet.");
            return;
        }

        await _udp.SendAsync(datagram, _latch.Target(_options.RemoteEndPoint), cancellationToken).ConfigureAwait(false);

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
