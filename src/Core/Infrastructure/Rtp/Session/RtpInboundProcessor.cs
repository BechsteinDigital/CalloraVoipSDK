using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// The inbound half of an <see cref="RtpSession"/>: takes one raw datagram off the media socket, runs the
/// RFC 7983 / RFC 5764 §5.1.2 demux (STUN / DTLS / RTP / RTCP share the 5-tuple), authenticates and decrypts
/// with the negotiated SRTP/SRTCP contexts, validates the RTP sequence (RFC 3550 §A.1), enforces the
/// fail-closed contract (no plaintext interpretation on a keyed leg before the handshake completes), and
/// dispatches the result through the session's callbacks. Owns the inbound security contexts and the
/// secondary-stream (RFC 4588 RTX) routing state; the socket, the send path, the symmetric-RTP latch and the
/// SSRC-collision reseed stay in <see cref="RtpSession"/>, which drives this via a single receive-loop thread.
/// </summary>
internal sealed class RtpInboundProcessor
{
    private readonly RtpSessionOptions _options;
    private readonly IRtpPacketCodec _codec;
    private readonly IRtcpPacketCodec _rtcpCodec;
    private readonly SymmetricRtpLatch _latch;
    private readonly RtpTrackedSsrcTable _ssrcTable;
    private readonly ILogger _logger;

    // Reads the session's current SSRC (RFC 3550 §5) for collision detection, and signals a detected collision
    // so the session — owner of the send-side sequence/timestamp/SSRC triple — performs the §8.2 reseed + BYE.
    private readonly Func<uint> _localSsrc;
    private readonly Action<uint> _onSsrcCollision;

    // Dispatch sinks. The session wires each to raise its (nullable, teardown-cleared) event, so subscriber
    // changes and disposal are reflected at call time and the public event keeps the session as its sender.
    private readonly Action<RtpPacket> _onPacketReceived;
    private readonly Action<IReadOnlyList<RtcpPacket>> _onRtcpCompound;
    private readonly Action<byte[], IPEndPoint> _onStun;
    private readonly Action<byte[], IPEndPoint> _onDtls;
    private readonly Action<RtpPacket> _onSecondary;

    // Inbound security contexts. Fixed from options for SDES/plain calls; the DTLS-SRTP path installs them once
    // after the handshake via InstallInbound. Written once by the handshake thread, read per packet on the
    // receive loop — reference reads/writes are atomic, Volatile ensures visibility.
    private ISrtpContext? _inboundSrtp;
    private ISrtcpContext? _inboundSrtcp;

    // Secondary multiplexed stream (RFC 4588 RTX): one additional payload type carried on the same socket with
    // its own inbound SRTP context, so its independent sequence space never shares the primary stream's replay
    // window / ROC. -1 until ConfigureSecondaryStream is called; context installed later like the primary one.
    private volatile int _secondaryPayloadType = -1;
    private ISrtpContext? _secondaryInboundSrtp;

    public RtpInboundProcessor(
        RtpSessionOptions options,
        IRtpPacketCodec codec,
        IRtcpPacketCodec rtcpCodec,
        SymmetricRtpLatch latch,
        RtpTrackedSsrcTable ssrcTable,
        ILogger logger,
        Func<uint> localSsrc,
        Action<uint> onSsrcCollision,
        Action<RtpPacket> onPacketReceived,
        Action<IReadOnlyList<RtcpPacket>> onRtcpCompound,
        Action<byte[], IPEndPoint> onStun,
        Action<byte[], IPEndPoint> onDtls,
        Action<RtpPacket> onSecondary)
    {
        _options = options;
        _codec = codec;
        _rtcpCodec = rtcpCodec;
        _latch = latch;
        _ssrcTable = ssrcTable;
        _logger = logger;
        _localSsrc = localSsrc;
        _onSsrcCollision = onSsrcCollision;
        _onPacketReceived = onPacketReceived;
        _onRtcpCompound = onRtcpCompound;
        _onStun = onStun;
        _onDtls = onDtls;
        _onSecondary = onSecondary;

        _inboundSrtp = options.InboundSrtp;
        _inboundSrtcp = options.InboundSrtcp;
    }

    /// <summary>
    /// Installs the inbound SRTP/SRTCP contexts negotiated after session start (DTLS-SRTP: keys exist only once
    /// the handshake completed, RFC 5764 §4.2). Called by <see cref="RtpSession.InstallSecurityContexts"/>.
    /// </summary>
    internal void InstallInbound(ISrtpContext inboundSrtp, ISrtcpContext inboundSrtcp)
    {
        Volatile.Write(ref _inboundSrtp, inboundSrtp);
        Volatile.Write(ref _inboundSrtcp, inboundSrtcp);
    }

    /// <summary>
    /// Installs the inbound SRTP context for the secondary (RFC 4588 RTX) stream. Called by
    /// <see cref="RtpSession.InstallSecondarySecurityContexts"/>.
    /// </summary>
    internal void InstallSecondaryInbound(ISrtpContext inbound)
        => Volatile.Write(ref _secondaryInboundSrtp, inbound);

    /// <summary>Routes inbound RTP of this payload type to the secondary path (RFC 4588 RTX).</summary>
    internal void ConfigureSecondaryStream(byte payloadType) => _secondaryPayloadType = payloadType;

    /// <summary>The configured secondary-stream payload type, or <c>null</c> when none.</summary>
    internal byte? SecondaryPayloadType => _secondaryPayloadType >= 0 ? (byte)_secondaryPayloadType : null;

    /// <summary>
    /// Processes one inbound datagram off the media socket: demux, decrypt, validate and dispatch. Runs on the
    /// session's single receive-loop thread; <paramref name="source"/> is the datagram's sender (null only on
    /// the deterministic test-injection path, which cannot exercise STUN/DTLS routing).
    /// </summary>
    internal void Process(ReadOnlySpan<byte> datagram, IPEndPoint? source)
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
                _onStun(stunDatagram, source);
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
                _onDtls(dtlsDatagram, source);
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
                catch (SrtpSourceLimitException)
                {
                    // Authenticated but a new SSRC beyond the per-context cap (#157 P1-2): clean drop,
                    // never a receive-loop kill; Debug-level so a keyed flood cannot flood the log.
                    _logger.LogDebug("Dropping SRTCP packet from {Source}: tracked-source cap reached.", source);
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

            IReadOnlyList<RtcpPacket> packets;
            try
            {
                // Decode the compound once; every subscriber shares this read-only list (no per-consumer
                // re-parse). A malformed compound is dropped — RTCP must never break the receive loop.
                packets = _rtcpCodec.Decode(rtcpDatagram);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                _logger.LogDebug("Dropping malformed inbound RTCP compound from {Source}: {Message}", source, ex.Message);
                return;
            }

            try
            {
                _onRtcpCompound(packets);
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
        // reaches the codec, the jitter buffer, or the symmetric-RTP latch. Snapshot the
        // context once so the latch's authenticity signal is consistent with this exact packet.
        var inboundSrtp = Volatile.Read(ref _inboundSrtp);
        if (_options.RequireEncryptedMedia && inboundSrtp is null)
        {
            // Fail closed (DTLS-SRTP before handshake completion): a keyed call must never
            // accept plaintext RTP — it would also poison the symmetric-RTP latch.
            _logger.LogDebug("Dropping inbound RTP from {Source}: encrypted media required but no SRTP context installed yet.", source);
            return;
        }

        if (inboundSrtp is not null)
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
            catch (SrtpSourceLimitException)
            {
                // Authenticated but a new SSRC beyond the per-context cap (#157 P1-2): clean drop,
                // never a receive-loop kill; Debug-level so a keyed flood cannot flood the log.
                _logger.LogDebug("Dropping SRTP packet from {Source}: tracked-source cap reached.", source);
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

        // SSRC collision detection + resolution (RFC 3550 §8.2): a third party is transmitting with our SSRC.
        if (packet.Ssrc == _localSsrc())
        {
            _onSsrcCollision(packet.Ssrc);
            return;
        }

        // Sequence number validation (RFC 3550 §A.1)
        var tracked = _ssrcTable.GetOrAdd(packet.Ssrc);
        var result = tracked.Validator.Validate(packet.SequenceNumber);
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

        // Symmetric-RTP latch (CVE-2017-14099 hardening): only a validated packet — not an SSRC collision, and
        // Valid/TooLate rather than a duplicate or sequence jump — may steer the outbound path. A change away
        // from an established source re-latches only on a keyed (authenticated) call; a plaintext call locks.
        // On a plaintext call a refused new source is also not admitted for delivery — a spoofed packet must not
        // reach the media consumer, not just be prevented from re-pointing the outbound path (#161 P1-4).
        if (source is not null && !_latch.Consider(source, authenticated: inboundSrtp is not null))
            return;

        try
        {
            _onPacketReceived(packet);
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
            catch (SrtpSourceLimitException)
            {
                // Authenticated but a new SSRC beyond the per-context cap (#157 P1-2): clean drop,
                // never a receive-loop kill; Debug-level so a keyed flood cannot flood the log.
                _logger.LogDebug("Dropping secondary SRTP packet from {Source}: tracked-source cap reached.", source);
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
            _onSecondary(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in secondary RTP handler.");
        }
    }
}
