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
    private readonly ConcurrentDictionary<string, Action<RtpPacket>> _sinksByMid = new(StringComparer.Ordinal);
    private long _droppedPackets;

    public BundledTrackRouter(BundledRtpDemultiplexer demultiplexer)
        => _demultiplexer = demultiplexer ?? throw new ArgumentNullException(nameof(demultiplexer));

    /// <summary>
    /// Count of inbound RTP packets that could not be routed — undemuxable (RFC 8843 §9.2), or resolved
    /// to a MID whose m-line has no registered sink.
    /// </summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>
    /// Registers the sink for one m-line's MID. The sink runs synchronously on the receive path via
    /// <see cref="DispatchInboundRtp"/> — it must not block or perform inline I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sink is already registered for <paramref name="mid"/>.</exception>
    public void RegisterTrack(string mid, Action<RtpPacket> sink)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_sinksByMid.TryAdd(mid, sink))
            throw new InvalidOperationException($"A track sink is already registered for MID '{mid}'.");
    }

    /// <summary>Removes the sink for a MID. Returns <see langword="false"/> when none was registered.</summary>
    public bool UnregisterTrack(string mid) => _sinksByMid.TryRemove(mid, out _);

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
            sink(packet);
            return true;
        }

        Interlocked.Increment(ref _droppedPackets);
        return false;
    }
}
