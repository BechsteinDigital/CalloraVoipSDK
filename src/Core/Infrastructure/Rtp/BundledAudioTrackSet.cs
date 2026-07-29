using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The set of <em>additional</em> inbound audio tracks on one BUNDLE media session (4.7.0: N audio m-lines,
/// RFC 8843 §9 — e.g. an SFU client receiving one audio stream per remote participant), and the wiring that
/// makes each one a receive sink on the shared transport. Each additional track is keyed by its own MID and
/// carried on its own bundle-wide-distinct SSRC; inbound packets are routed to the owning track by the MID
/// header extension (RFC 9143) when tracks share a payload type. The set owns the extra MIDs, registers each
/// one's inbound router sink and (symmetric) outbound sender at construction, dispatches inbound packets on the
/// mid-tagged event, and drives the internal per-MID send seam.
/// </summary>
/// <remarks>
/// This is the slim audio pendant to <see cref="BundledVideoTrackSet"/>. Audio has no per-frame reassembly,
/// key-frame/PLI feedback, RTX, or simulcast, so an additional audio track is a bare inbound sink — the set
/// stores no per-track object, only the MIDs, and registers the router sink directly. It is extracted from
/// <see cref="BundledMediaSession"/> so that session stays a wiring/lifecycle unit under the 1000-line rule.
/// <para>
/// The <em>primary</em> audio m-line is NOT part of this set: it anchors the bundle transport (ICE ufrag/pwd,
/// DTLS fingerprint and role ride it, RFC 8843) and is addressed by the session's mid-less <c>AudioReceived</c>
/// event and the send/DTMF facade for backward compatibility with the pre-4.7.0 single-audio path. This set
/// holds only the extra receive-only sinks that sit alongside that anchor; RFC 4733 telephone-event (DTMF) is
/// intentionally not reassembled here — it stays on the primary.
/// </para>
/// <para>
/// Thread-safety: the MID map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> so the receive loop reads it
/// lock-free while a mid-call renegotiation (4.7.0 Slice 3) extends or prunes it via <see cref="TryAdd"/> /
/// <see cref="Remove"/> without tearing an enumeration. Those mutations run under the session's own track-mutation
/// gate (which orders control-plane changes against each other, never against the receive loop). A throwing inbound
/// subscriber is caught and logged so it never tears down the shared receive loop (K3), matching the primary and
/// video paths.
/// </para>
/// </remarks>
internal sealed class BundledAudioTrackSet
{
    // The additional audio MIDs (the primary is not held here). A ConcurrentDictionary keyed by MID so a later
    // live-add (a subsequent slice) stays lock-free against the receive loop; the value is the MID itself (audio
    // needs no per-track object — the inbound sink lives on the router, the outbound sender on the pipeline).
    private readonly ConcurrentDictionary<string, string> _byMid;

    // The MIDs in insertion order, so Mids stays stably ordered (a ConcurrentDictionary does not guarantee
    // insertion-order enumeration). Snapshotted under the lock for diagnostics, never touched on the media hot path.
    private readonly object _orderGate = new();
    private readonly List<string> _midOrder = [];

    private readonly BundledOutboundPipeline _outbound;
    private readonly ILogger _logger;

    /// <summary>Creates an empty set (a bundle with only the primary audio m-line). No wiring is registered.</summary>
    /// <param name="outbound">The bundle's outbound pipeline (unused until additional tracks exist).</param>
    /// <param name="logger">Logs a throwing inbound subscriber without propagating it (K3).</param>
    public BundledAudioTrackSet(BundledOutboundPipeline outbound, ILogger logger)
    {
        _byMid = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates the set from the additional audio m-line configurations and wires each one onto the shared
    /// transport: it registers the MID's inbound router sink (raising <paramref name="raiseFrameReceived"/> tagged
    /// with the MID) and its symmetric outbound sender on <paramref name="outbound"/>. The primary audio m-line's
    /// MID must not be included — it is the anchor and is wired separately by the session. The caller must extend
    /// the demux boundary for these MIDs before the sinks fire (the session does so when it builds the router).
    /// </summary>
    /// <param name="options">The negotiated bundle options (carries the additional audio configs and header-ext ids).</param>
    /// <param name="router">The MID→sink router the inbound sinks register on.</param>
    /// <param name="outbound">The outbound pipeline the symmetric per-MID senders register on.</param>
    /// <param name="raiseFrameReceived">Raises the session's mid-tagged inbound-audio event (MID, packet).</param>
    /// <param name="logger">Logs a throwing inbound subscriber without propagating it (K3).</param>
    /// <exception cref="ArgumentException">Two additional audio configs share a MID.</exception>
    public BundledAudioTrackSet(
        BundledMediaSessionOptions options,
        BundledTrackRouter router,
        BundledOutboundPipeline outbound,
        Action<string, RtpPacket> raiseFrameReceived,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(raiseFrameReceived);
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _byMid = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        foreach (var audio in options.AdditionalAudioTracks)
        {
            var mid = audio.Mid;
            if (!_byMid.TryAdd(mid, mid))
                throw new ArgumentException($"Duplicate additional audio MID '{mid}' in the bundle.", nameof(options));
            _midOrder.Add(mid);

            // A bare receive sink: dispatch each inbound packet on the mid-tagged event, guarded so a throwing
            // subscriber never tears down the shared receive loop (K3). DTMF is NOT reassembled here (stays on the
            // primary). The symmetric outbound sender lets the same session emit on the MID over a loopback peer;
            // there is no public N-audio send API in this slice (that is a later slice).
            router.RegisterTrack(mid, packet => Dispatch(mid, packet, raiseFrameReceived));
            outbound.RegisterTrack(mid, BundledMediaSessionComposition.BuildOutboundTrack(options, audio));
        }
    }

    private void Dispatch(string mid, RtpPacket packet, Action<string, RtpPacket> raiseFrameReceived)
    {
        try
        {
            raiseFrameReceived(mid, packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled audio AudioTrackFrameReceived handler for MID '{Mid}'.", mid);
        }
    }

    /// <summary>
    /// Records an additional audio MID as live (4.7.0 renegotiation): the session has already registered the MID's
    /// inbound router sink and outbound sender under its track-mutation gate, so this only publishes the MID into
    /// the set so <see cref="Contains"/>/<see cref="Mids"/>/<see cref="SendAsync"/> find it. Returns
    /// <see langword="false"/> when a track with that MID is already present (the caller holds the gate, so this is
    /// an unexpected race it unwinds). The caller MUST hold the session's track-mutation gate.
    /// </summary>
    /// <param name="mid">The additional audio track's MID (never the primary anchor's MID).</param>
    public bool TryAdd(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (!_byMid.TryAdd(mid, mid))
            return false;
        lock (_orderGate)
            _midOrder.Add(mid);
        return true;
    }

    /// <summary>
    /// Removes an additional audio MID from the set (4.7.0 renegotiation): the session has already unregistered the
    /// MID's inbound router sink and outbound sender under its track-mutation gate, so this only drops the MID from
    /// the set. Idempotent — a no-op when the MID is not present. The caller MUST hold the session's track-mutation
    /// gate. Audio has no per-track object to dispose (the sink lives on the router, the sender on the pipeline), so
    /// unlike video there is no deferred-dispose concern here.
    /// </summary>
    /// <param name="mid">The additional audio track's MID to remove.</param>
    public void Remove(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (_byMid.TryRemove(mid, out _))
            lock (_orderGate)
                _midOrder.Remove(mid);
    }

    /// <summary>Whether the bundle carries at least one additional inbound audio track (beyond the primary).</summary>
    public bool Any => !_byMid.IsEmpty;

    /// <summary>The number of additional inbound audio tracks on the bundle (excluding the primary).</summary>
    public int Count => _byMid.Count;

    /// <summary>Whether <paramref name="mid"/> is one of the additional inbound audio tracks. Lock-free.</summary>
    public bool Contains(string mid)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        return _byMid.ContainsKey(mid);
    }

    /// <summary>
    /// The additional audio MIDs as a point-in-time snapshot in insertion order. Diagnostics/enumeration, not
    /// the media hot path.
    /// </summary>
    public IReadOnlyList<string> Mids
    {
        get { lock (_orderGate) return _midOrder.ToArray(); }
    }

    /// <summary>
    /// Internal send seam: sends one audio RTP payload on the additional audio track <paramref name="mid"/>
    /// through its registered outbound sender (suppressed until DTLS keys the transport like every bundle send).
    /// Drives the symmetric sender wired at construction; there is no public N-audio send API in this slice.
    /// </summary>
    /// <exception cref="InvalidOperationException">This bundle has no additional audio track with that MID.</exception>
    public ValueTask SendAsync(string mid, ReadOnlyMemory<byte> payload, bool marker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mid);
        if (!_byMid.ContainsKey(mid))
            throw new InvalidOperationException($"This bundle has no additional audio track with MID '{mid}'.");
        return _outbound.SendAsync(mid, payload, marker, cancellationToken: cancellationToken);
    }
}
