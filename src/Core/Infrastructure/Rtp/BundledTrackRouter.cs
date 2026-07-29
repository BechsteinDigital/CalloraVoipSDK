using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Routes inbound RTP packets on a BUNDLE transport to per-m-line track sinks. It pairs the RFC 8843
/// §9.2 <see cref="BundledRtpDemultiplexer"/> (which resolves a packet's MID) with a MID→sink registry:
/// each track — audio, video — registers a sink for its MID, and every inbound packet is dispatched to
/// the matching sink, or dropped and counted when it cannot be associated or its m-line has no sink.
///
/// This is the track-routing sublayer of the bundled transport (ADR-010 B2b): it owns no socket and no
/// DTLS/ICE — the shared 5-tuple that feeds it is assembled in later slices; here it only decides which
/// track an already-demuxed RTP packet belongs to. Thread-safe: the registry is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> (registration happens at wiring time, dispatch reads
/// it on the receive path) and the drop counter is updated with <see cref="Interlocked"/>.
/// </summary>
internal sealed class BundledTrackRouter
{
    private readonly BundledRtpDemultiplexer _demultiplexer;
    // Each sink receives the packet plus its resolved RID (RFC 8852 <c>a=rid</c>), or null when no simulcast
    // encoding was negotiated or the packet carries no RID and its SSRC is not yet latched. Simple sinks
    // (audio, non-simulcast video) ignore the RID; a simulcast-aware video sink demultiplexes on it.
    private readonly ConcurrentDictionary<string, Action<RtpPacket, string?>> _sinksByMid = new(StringComparer.Ordinal);
    private long _droppedPackets;

    public BundledTrackRouter(BundledRtpDemultiplexer demultiplexer)
        => _demultiplexer = demultiplexer ?? throw new ArgumentNullException(nameof(demultiplexer));

    /// <summary>
    /// Count of inbound RTP packets that could not be routed — undemuxable (RFC 8843 §9.2), or resolved
    /// to a MID whose m-line has no registered sink.
    /// </summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>
    /// Registers a RID-unaware sink for one m-line's MID. The resolved RID is ignored — the back-compat
    /// overload for audio and non-simulcast video sinks. The sink runs synchronously on the receive path
    /// via <see cref="DispatchInboundRtp"/> — it must not block or perform inline I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sink is already registered for <paramref name="mid"/>.</exception>
    public void RegisterTrack(string mid, Action<RtpPacket> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        RegisterTrack(mid, (packet, _) => sink(packet));
    }

    /// <summary>
    /// Registers a RID-aware sink for one m-line's MID (RFC 8852 <c>a=rid</c>): the sink receives each
    /// packet with its resolved RID, or null when the packet carries no RID and simulcast demultiplexing
    /// therefore does not apply. Used by the video track to split simulcast encodings under one MID
    /// (RFC 8853). The sink runs synchronously on the receive path via <see cref="DispatchInboundRtp"/> —
    /// it must not block or perform inline I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sink is already registered for <paramref name="mid"/>.</exception>
    public void RegisterTrack(string mid, Action<RtpPacket, string?> sink)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_sinksByMid.TryAdd(mid, sink))
            throw new InvalidOperationException($"A track sink is already registered for MID '{mid}'.");
    }

    /// <summary>Removes the sink for a MID. Returns <see langword="false"/> when none was registered.</summary>
    public bool UnregisterTrack(string mid) => _sinksByMid.TryRemove(mid, out _);

    /// <summary>
    /// Starts accepting inbound RTP for a MID added mid-call (RFC 8843 §9.2 / RFC 8829 renegotiation, P3b) by
    /// extending the underlying <see cref="BundledRtpDemultiplexer"/>'s accepted-MID set. Call this
    /// <em>before</em> <see cref="RegisterTrack(string, Action{RtpPacket})"/> so the first packets of the new stream demultiplex (rather
    /// than being rejected as an unknown MID) — until a sink is registered they are dropped and counted, never
    /// crash. Thread-safe against the receive loop and idempotent (an already-known MID is a no-op).
    /// </summary>
    /// <param name="mid">The MID token of the newly negotiated m-line to start accepting.</param>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is <see langword="null"/> or empty.</exception>
    public void AddKnownMid(string mid) => _demultiplexer.AddKnownMid(mid);

    /// <summary>
    /// Dispatches one inbound RTP packet to its m-line's sink. Returns <see langword="false"/> (and
    /// increments <see cref="DroppedPackets"/>) when the packet cannot be associated to an m-line or that
    /// m-line has no registered sink.
    /// </summary>
    public bool DispatchInboundRtp(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_demultiplexer.TryResolveMid(packet, out var mid)
            && _sinksByMid.TryGetValue(mid, out var sink))
        {
            // Resolve the simulcast RID only when an encoding was negotiated (RFC 8853); otherwise skip it so
            // the non-simulcast dispatch stays byte-identical — rid is always null and RID-unaware sinks ignore it.
            var rid = _demultiplexer.RidDemuxEnabled && _demultiplexer.TryResolveRid(packet, out var resolved)
                ? resolved
                : null;
            sink(packet, rid);
            return true;
        }

        Interlocked.Increment(ref _droppedPackets);
        return false;
    }
}
