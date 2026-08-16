using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The outbound datagram path of the bundled transport (ADR-011 B2c-in-4, RFC 8843): it hosts the
/// per-m-line <see cref="BundledOutboundTrack"/>s, encrypts each built packet with the transport's
/// shared outbound SRTP context (per-SSRC state, ADR-011 B2c-in-1), and sends it over the shared
/// 5-tuple via <see cref="IBundledDatagramSender"/>. It is the send-side mirror of
/// <see cref="BundledInboundPipeline"/> and, like it, owns no socket — the shared UDP send is provided
/// by the sender seam wired in B3.
///
/// BUNDLE media is DTLS-SRTP only, so the pipeline fails closed: a send is suppressed and counted until
/// <see cref="InstallOutboundKey"/> supplies the negotiated SRTP context, and never leaves as plaintext.
/// Thread-safe: the track registry is a <see cref="ConcurrentDictionary{TKey,TValue}"/>, the SRTP
/// context is read through <see cref="Volatile"/> and is internally synchronised, and each track
/// advances its own RTP cursors under its own lock.
/// </summary>
internal sealed class BundledOutboundPipeline
{
    // Routed by (MID, RID): a non-simulcast m-line registers one track under (mid, null); a simulcast
    // m-line (RFC 8853) registers one track per a=rid layer under (mid, rid), each with its own SSRC.
    private readonly ConcurrentDictionary<BundledOutboundTrackKey, BundledOutboundTrack> _tracks = new();
    private readonly IRtpPacketCodec _codec;
    private readonly IBundledDatagramSender _sender;
    private readonly bool _stampsTransportCc;
    private readonly ILogger<BundledOutboundPipeline> _logger;

    // The transport-wide-cc sequence counter (transport-cc): a SINGLE monotonic counter shared
    // across every track on this bundled transport, since transport-cc numbers the whole transport, not a
    // per-stream stream. -1 before the first stamped send so the first packet takes sequence 0. Advanced with
    // Interlocked because concurrent sends on different tracks share it. Only used when transport-cc was
    // negotiated (_stampsTransportCc); otherwise no extension is stamped and the wire bytes are unchanged.
    private int _transportCcSequence = -1;

    private ISrtpContext? _outboundSrtp;
    private ISrtcpContext? _outboundSrtcp;
    private long _suppressedSends;
    private long _packetsSent;
    private long _bytesSent;
    private long _rtcpPacketsSent;
    private long _rtcpSuppressedSends;

    /// <summary>Raised after a packet has actually been sent, so an RTX buffer (RFC 4588) can retain it.</summary>
    public event Action<RtpPacket>? PacketSent;

    /// <param name="codec">Encodes each built RTP packet before SRTP protection.</param>
    /// <param name="sender">The shared 5-tuple send seam (B3).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="stampsTransportCc">
    /// Whether outgoing media packets carry a transport-wide-cc sequence number (transport-cc): true
    /// only when the <c>a=extmap</c> for the transport-wide extension was negotiated on the bundle. When false
    /// no counter is advanced and the wire bytes are byte-identical to before (the tracks stamp MID/RID only).
    /// </param>
    public BundledOutboundPipeline(
        IRtpPacketCodec codec,
        IBundledDatagramSender sender,
        ILogger<BundledOutboundPipeline> logger,
        bool stampsTransportCc = false)
    {
        _codec  = codec  ?? throw new ArgumentNullException(nameof(codec));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stampsTransportCc = stampsTransportCc;
    }

    /// <summary>Sends suppressed because no outbound SRTP context was installed yet (fail-closed).</summary>
    public long SuppressedSends => Interlocked.Read(ref _suppressedSends);

    /// <summary>Total RTP packets actually sent (after SRTP protection) across all tracks.</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Total bytes of protected RTP datagrams sent across all tracks.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Total protected SRTCP datagrams sent (RFC 3550 §6.4 Sender Reports, SDES).</summary>
    public long RtcpPacketsSent => Interlocked.Read(ref _rtcpPacketsSent);

    /// <summary>RTCP sends suppressed because no outbound SRTCP context was installed yet (fail-closed).</summary>
    public long RtcpSuppressedSends => Interlocked.Read(ref _rtcpSuppressedSends);

    /// <summary>
    /// Registers the non-simulcast outbound track for one m-line's MID.
    /// </summary>
    /// <exception cref="InvalidOperationException">A track is already registered for <paramref name="mid"/>.</exception>
    public void RegisterTrack(string mid, BundledOutboundTrack track) => RegisterTrack(mid, rid: null, track);

    /// <summary>
    /// Registers the outbound track for one m-line's MID and simulcast <paramref name="rid"/> layer
    /// (RFC 8853); a <see langword="null"/> <paramref name="rid"/> is the non-simulcast single stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">A track is already registered for that MID/RID.</exception>
    public void RegisterTrack(string mid, string? rid, BundledOutboundTrack track)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(track);
        if (!_tracks.TryAdd(new BundledOutboundTrackKey(mid, rid), track))
            throw new InvalidOperationException(
                $"An outbound track is already registered for MID '{mid}'{(rid is null ? "" : $" RID '{rid}'")}.");
    }

    /// <summary>
    /// Removes exactly one registration — the non-simulcast stream (<paramref name="rid"/> null) or one
    /// <c>a=rid</c> layer of a MID. Used to unwind a partially registered track without touching the MID's
    /// other layers. Returns <see langword="false"/> when nothing was registered under that key.
    /// </summary>
    public bool UnregisterTrack(string mid, string? rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        return _tracks.TryRemove(new BundledOutboundTrackKey(mid, rid), out _);
    }

    /// <summary>Removes every track (all RID layers) for a MID. Returns <see langword="false"/> when none was registered.</summary>
    public bool UnregisterTrack(string mid)
    {
        var removed = false;
        foreach (var key in _tracks.Keys)
            if (string.Equals(key.Mid, mid, StringComparison.Ordinal))
                removed |= _tracks.TryRemove(key, out _);
        return removed;
    }

    /// <summary>
    /// Whether <paramref name="ssrc"/> is a sending SSRC of the given MID — the non-simulcast stream, or any
    /// of that m-line's <c>a=rid</c> layers (RFC 8853). On a BUNDLE one RTCP channel carries the feedback for
    /// every m-line, so an inbound PLI/FIR/NACK has to be routed by the media SSRC it names; this answers
    /// "is that one of mine?" for the track that asks.
    /// </summary>
    public bool OwnsSsrc(string mid, uint ssrc)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        foreach (var entry in _tracks)
        {
            if (entry.Value.Ssrc == ssrc && string.Equals(entry.Key.Mid, mid, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Installs the shared outbound SRTP context once the DTLS-SRTP handshake has derived the key. Until
    /// then every send fails closed. The one context serves every track's SSRC under the shared key.
    /// </summary>
    public void InstallOutboundKey(ISrtpContext srtp)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        Volatile.Write(ref _outboundSrtp, srtp);
    }

    /// <summary>
    /// Installs the shared outbound SRTCP context derived by the same DTLS-SRTP handshake (RFC 3711 §3.4).
    /// Until then every RTCP send (Sender Reports, SDES) fails closed. The one context serves every SSRC's
    /// RTCP under the shared key and carries its own SRTCP index.
    /// </summary>
    public void InstallOutboundRtcpKey(ISrtcpContext srtcp)
    {
        ArgumentNullException.ThrowIfNull(srtcp);
        Volatile.Write(ref _outboundSrtcp, srtcp);
    }

    /// <summary>
    /// Protects a plaintext RTCP compound packet with the shared outbound SRTCP context and sends it over the
    /// shared 5-tuple (RFC 3550 §6, RFC 3711 §3.4). Fails closed: until <see cref="InstallOutboundRtcpKey"/>
    /// supplies the key — or if the context is disposed mid-send during teardown — the packet is suppressed
    /// and counted, never leaving as plaintext.
    /// <para>
    /// Returns what actually happened (#162 P2-5). A suppressed compound never reached the wire, so the
    /// periodic reporter must not commit reporting state for it — see <see cref="RtcpSendOutcome"/>.
    /// </para>
    /// </summary>
    public async ValueTask<RtcpSendOutcome> SendRtcpAsync(ReadOnlyMemory<byte> rtcp, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _outboundSrtcp) is not { } outboundSrtcp)
        {
            Interlocked.Increment(ref _rtcpSuppressedSends);
            _logger.LogDebug("Suppressing outbound RTCP: no SRTCP context installed yet.");
            return RtcpSendOutcome.Suppressed;
        }

        byte[] datagram;
        try
        {
            datagram = outboundSrtcp.ProtectRtcp(rtcp.Span);
        }
        catch (ObjectDisposedException)
        {
            // A send racing transport teardown after the context owner zeroed the keys — suppress the
            // packet; never fall through to an unprotected RTCP send.
            Interlocked.Increment(ref _rtcpSuppressedSends);
            _logger.LogDebug("Suppressing outbound RTCP: SRTCP context disposed during teardown.");
            return RtcpSendOutcome.Suppressed;
        }
        catch (SrtpKeyLifetimeExceededException ex)
        {
            // Per-key SRTCP index budget exhausted (RFC 3711 §9.2, #157 P1-1): fail closed — no reused
            // keystream, no plaintext RTCP. RTCP goes silent until rekey.
            Interlocked.Increment(ref _rtcpSuppressedSends);
            _logger.LogError(ex, "Suppressing outbound RTCP: SRTCP key lifetime exhausted; media requires rekey.");
            return RtcpSendOutcome.Suppressed;
        }
        // Any other ProtectRtcp fault (a cryptographic/argument error) is deliberately NOT caught here: it
        // propagates to the periodic reporter's catch-all, which logs and retries next interval. The invariant
        // that matters is met either way — the send only ever happens on a successfully protected datagram, so
        // no plaintext RTCP can leave. Only the disposed-context race is suppressed silently, as a teardown.

        await _sender.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _rtcpPacketsSent);
        return RtcpSendOutcome.Sent;
    }

    /// <summary>
    /// Snapshots the per-SSRC Sender Report counters (RFC 3550 §6.4.1) of every track that has sent at least
    /// one packet, for the periodic RTCP reporter to build Sender Reports from. Tracks that have not sent are
    /// omitted (no SR is due for them).
    /// </summary>
    public IReadOnlyList<BundledSenderReportInfo> SnapshotSenderReports()
    {
        var reports = new List<BundledSenderReportInfo>();
        foreach (var track in _tracks.Values)
        {
            // One lock per track: the four counters are captured atomically so a concurrent send cannot tear
            // the packet/octet/timestamp trio across the report (fixes the prior four-separate-lock read).
            if (track.Snapshot() is { } info)
                reports.Add(info);
        }

        return reports;
    }

    /// <summary>
    /// Sends one media packet on the given MID's track, advancing that track's timestamp cursor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for <paramref name="mid"/>.</exception>
    public ValueTask SendAsync(
        string mid,
        ReadOnlyMemory<byte> payload,
        bool marker = false,
        byte? payloadTypeOverride = null,
        string? rid = null,
        CancellationToken cancellationToken = default)
    {
        var track = ResolveTrack(mid, rid);
        var payloadType = payloadTypeOverride ?? track.DefaultPayloadType;
        return SendCoreAsync(mid, track, payload, marker, payloadType, timestampOverride: null, advanceTimestamp: true, cancellationToken);
    }

    /// <summary>
    /// Sends one packet on the given MID's track (optionally a simulcast <paramref name="rid"/> layer) with
    /// an explicit timestamp and without advancing the track's timestamp cursor — for video frames whose
    /// packets share one frame-level timestamp.
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for that MID/RID.</exception>
    public ValueTask SendTimestampedAsync(
        string mid,
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint timestamp,
        string? rid = null,
        CancellationToken cancellationToken = default)
    {
        var track = ResolveTrack(mid, rid);
        return SendCoreAsync(mid, track, payload, marker, payloadType, timestampOverride: timestamp, advanceTimestamp: false, cancellationToken);
    }

    /// <summary>
    /// Sends one audio RTP payload on the given MID's track with an explicit <paramref name="timestamp"/> and
    /// without advancing the track's timestamp cursor (RFC 3550 §5.1). This is the audio pendant to
    /// <see cref="SendTimestampedAsync"/>: it resolves the track's default payload type (like the frameless
    /// <see cref="SendAsync"/>) instead of taking an explicit one, so an SFU forwarding an added-audio stream
    /// preserves the source's timestamp (A/V-sync against forwarded video) rather than re-clocking on the cursor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for <paramref name="mid"/>.</exception>
    public ValueTask SendAudioTimestampedAsync(
        string mid,
        ReadOnlyMemory<byte> payload,
        uint timestamp,
        bool marker = false,
        CancellationToken cancellationToken = default)
    {
        var track = ResolveTrack(mid, rid: null);
        return SendCoreAsync(mid, track, payload, marker, track.DefaultPayloadType, timestampOverride: timestamp, advanceTimestamp: false, cancellationToken);
    }

    /// <summary>
    /// Reserves <paramref name="units"/> of the (non-simulcast) track's timestamp space for an out-of-band
    /// RFC 4733 telephone-event registered for <paramref name="mid"/>: returns the current cursor to stamp the
    /// event on the audio stream's clock (RFC 4733 §2.1) and advances the cursor past the event so a following
    /// event or media packet is distinctly timestamped rather than folded into this one (§2.5.1.4).
    /// </summary>
    /// <exception cref="InvalidOperationException">No track is registered for <paramref name="mid"/>.</exception>
    public uint ReserveTrackTimestamp(string mid, uint units) => ResolveTrack(mid, rid: null).ReserveTimestamp(units);

    /// <summary>
    /// Sends one already-built RTX repair packet (RFC 4588 §4) over the shared transport: it carries its own
    /// SSRC, rtx payload type, and rtx sequence number and is <em>not</em> routed through a track (no MID/RID
    /// stamp, no cursor advance) — the peer demuxes the repair stream by payload type and SSRC via the
    /// <c>a=ssrc-group:FID</c> pairing (RFC 5576), not the MID. It rides the same shared outbound SRTP context
    /// as the primary streams: that context keys ROC and replay per SSRC (RFC 3711 §3.2.1), so the fresh rtx
    /// SSRC simply gets its own state — no separate context is needed. Fails closed exactly like a media send:
    /// until the DTLS-SRTP key is installed — or if the context is disposed mid-send during teardown — the
    /// packet is suppressed and counted, never leaving as plaintext.
    /// </summary>
    public async ValueTask SendRtxAsync(RtpPacket rtx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rtx);

        if (Volatile.Read(ref _outboundSrtp) is not { } outboundSrtp)
        {
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTX: no SRTP context installed yet.");
            return;
        }

        var datagram = _codec.Encode(rtx);
        try
        {
            datagram = outboundSrtp.Protect(datagram);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTX: SRTP context disposed during teardown.");
            return;
        }
        catch (SrtpKeyLifetimeExceededException ex)
        {
            // Per-key packet budget exhausted (RFC 3711 §9.2, #157 P1-1): fail closed — no reused
            // keystream, no plaintext. The RTX repair leg goes silent until rekey.
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogError(ex, "Suppressing outbound RTX: SRTP key lifetime exhausted; media requires rekey.");
            return;
        }

        await _sender.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _packetsSent);
        Interlocked.Add(ref _bytesSent, datagram.Length);
    }

    private BundledOutboundTrack ResolveTrack(string mid, string? rid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (!_tracks.TryGetValue(new BundledOutboundTrackKey(mid, rid), out var track))
            throw new InvalidOperationException(
                $"No outbound track is registered for MID '{mid}'{(rid is null ? "" : $" RID '{rid}'")}.");
        return track;
    }

    private async ValueTask SendCoreAsync(
        string mid,
        BundledOutboundTrack track,
        ReadOnlyMemory<byte> payload,
        bool marker,
        byte payloadType,
        uint? timestampOverride,
        bool advanceTimestamp,
        CancellationToken cancellationToken)
    {
        // Fail closed before building anything: a BUNDLE transport is DTLS-SRTP only and must never
        // emit plaintext RTP. Checked first so the track's sequence cursor is not consumed on a drop.
        if (Volatile.Read(ref _outboundSrtp) is not { } outboundSrtp)
        {
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTP on MID {Mid}: no SRTP context installed yet.", mid);
            return;
        }

        // Allocate the transport-wide-cc sequence number (transport-cc) for this packet from the one
        // shared counter — only when negotiated, and only after the fail-closed check so a suppressed send does
        // not consume a number (a hole in the transport-wide sequence would look like loss to the peer's
        // congestion estimator). RTX repair packets are deliberately never numbered here (SendRtxAsync bypasses
        // this path), matching the single-stream reference where only primary media carries the extension.
        ushort? transportCcSequence = _stampsTransportCc
            ? unchecked((ushort)Interlocked.Increment(ref _transportCcSequence))
            : null;

        var packet = track.BuildPacket(payload, marker, payloadType, timestampOverride, advanceTimestamp, transportCcSequence);
        var datagram = _codec.Encode(packet);

        try
        {
            datagram = outboundSrtp.Protect(datagram);
        }
        catch (ObjectDisposedException)
        {
            // A send racing transport teardown after the context owner zeroed the keys — suppress the
            // packet; never fall through to an unprotected send.
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogDebug("Suppressing outbound RTP: SRTP context disposed during teardown.");
            return;
        }
        catch (SrtpKeyLifetimeExceededException ex)
        {
            // Per-key packet budget exhausted (RFC 3711 §9.2, #157 P1-1): fail closed — no reused
            // keystream, no plaintext. The media leg goes silent until rekey.
            Interlocked.Increment(ref _suppressedSends);
            _logger.LogError(ex, "Suppressing outbound RTP: SRTP key lifetime exhausted; media requires rekey.");
            return;
        }

        await _sender.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _packetsSent);
        Interlocked.Add(ref _bytesSent, datagram.Length);
        // Advance this track's Sender Report counters (RFC 3550 §6.4.1): the octet count is the RTP payload
        // only, not the SRTP-protected datagram length; the RTP timestamp is the one just sent.
        track.RecordSent(payload.Length, packet.Timestamp);

        try
        {
            PacketSent?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled RTP PacketSent handler.");
        }
    }
}
