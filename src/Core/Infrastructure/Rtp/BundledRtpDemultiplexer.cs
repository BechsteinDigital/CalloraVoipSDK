using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Routes an inbound RTP packet on a BUNDLE transport (RFC 8843) to the media section it belongs to,
/// identified by its <c>a=mid</c> token. Implements the RFC 8843 §9.2 association order: an SSRC that is
/// already associated stays on its m-line (the hot path — MID is only stamped on the first packets of a
/// stream); otherwise the MID header extension (RFC 9143) associates the SSRC; otherwise a payload type
/// that unambiguously belongs to one m-line does. A packet that carries an explicit but unknown MID is
/// rejected (it is for an m-line this session does not have), and one that cannot be associated at all is
/// undemuxable.
///
/// This is the routing brain of the bundled transport (ADR-010 B2) — pure decision logic over the
/// negotiated maps plus a learned SSRC→MID table; it moves no bytes and owns no socket. Thread-safe: the
/// learned table and the accepted-MID set are <see cref="ConcurrentDictionary{TKey,TValue}"/> instances
/// (lock-free reads on the receive path); the payload-type map is immutable after construction. The
/// accepted-MID set is live-extensible via <see cref="AddKnownMid"/> for mid-call track additions
/// (RFC 8843 §9.2 / RFC 8829 renegotiation, P3b) without weakening the demux security boundary.
/// </summary>
internal sealed class BundledRtpDemultiplexer
{
    private readonly byte _midExtensionId;

    // Accepted m-line MIDs, used as a set (value byte is ignored). ConcurrentDictionary gives lock-free
    // ContainsKey reads on the receive hot path while AddKnownMid extends it via TryAdd (P3b).
    private readonly ConcurrentDictionary<string, byte> _knownMids;
    private readonly IReadOnlyDictionary<int, string> _payloadTypeToMid;
    private readonly ConcurrentDictionary<uint, string> _ssrcToMid = new();

    /// <summary>
    /// Creates the demultiplexer from the negotiated BUNDLE parameters.
    /// </summary>
    /// <param name="midExtensionId">
    /// The negotiated one-byte <c>a=extmap</c> id for the MID header extension (RFC 9143). <c>0</c> means
    /// no MID extension was negotiated — association then relies on SSRC and payload type only.
    /// </param>
    /// <param name="knownMids">The MID tokens of this session's m-lines (e.g. <c>audio</c>, <c>video</c>).</param>
    /// <param name="payloadTypeToMid">
    /// Payload types that unambiguously belong to exactly one m-line, mapped to that m-line's MID. The
    /// caller must exclude any payload type shared across m-lines — an ambiguous PT cannot demultiplex.
    /// </param>
    public BundledRtpDemultiplexer(
        byte midExtensionId,
        IReadOnlySet<string> knownMids,
        IReadOnlyDictionary<int, string> payloadTypeToMid)
    {
        _midExtensionId = midExtensionId;
        ArgumentNullException.ThrowIfNull(knownMids);
        _payloadTypeToMid = payloadTypeToMid ?? throw new ArgumentNullException(nameof(payloadTypeToMid));

        // Copy the caller's fixed set into a mutable, thread-safe set so it can be extended live (P3b)
        // while the receive loop reads it lock-free. The ctor parameter type stays IReadOnlySet<string>
        // so existing callers/factory are unaffected.
        _knownMids = new ConcurrentDictionary<string, byte>();
        foreach (var knownMid in knownMids)
        {
            _knownMids.TryAdd(knownMid, 0);
        }
    }

    /// <summary>
    /// Extends the set of accepted m-line MIDs for a track added mid-call (RFC 8843 §9.2 / RFC 8829
    /// renegotiation, P3b). Only MIDs from an already-negotiated description may be passed — the caller is
    /// responsible for ensuring that no invalid MID weakens the demux boundary. Thread-safe against the
    /// receive loop (lock-free <c>TryAdd</c>) and idempotent: passing an already-known MID is a no-op and
    /// does not throw.
    /// </summary>
    /// <param name="mid">The MID token of the newly negotiated m-line to start accepting.</param>
    /// <exception cref="ArgumentException"><paramref name="mid"/> is <see langword="null"/> or empty.</exception>
    public void AddKnownMid(string mid)
    {
        if (string.IsNullOrEmpty(mid))
        {
            throw new ArgumentException("MID must not be null or empty.", nameof(mid));
        }

        _knownMids.TryAdd(mid, 0);
    }

    /// <summary>
    /// Resolves the MID for an inbound RTP packet, learning the SSRC→MID association on the way. Returns
    /// <see langword="false"/> when the packet carries an explicit unknown MID, or cannot be associated by
    /// SSRC, MID, or payload type — the caller then drops it.
    /// </summary>
    public bool TryResolveMid(RtpPacket packet, out string mid)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return TryResolveMid(packet.Ssrc, packet.PayloadType, packet.HeaderExtension, out mid);
    }

    /// <summary>
    /// Resolves the MID from the demux keys directly (SSRC, payload type, header extension). The granular
    /// form used by <see cref="TryResolveMid(RtpPacket, out string)"/> and testable in isolation.
    /// </summary>
    public bool TryResolveMid(uint ssrc, int payloadType, RtpExtension? headerExtension, out string mid)
    {
        // 1. Already associated: the stable hot path (MID is absent on most packets of a stream).
        if (_ssrcToMid.TryGetValue(ssrc, out var associated))
        {
            mid = associated;
            return true;
        }

        // 2. MID header extension (RFC 9143): a known MID associates the SSRC; an explicit unknown MID is
        //    for an m-line we do not have (RFC 8843 §9.2) → drop, do not fall through to the PT guess.
        if (_midExtensionId != 0
            && RtpMidHeaderExtension.TryRead(headerExtension, _midExtensionId, out var readMid))
        {
            if (!_knownMids.ContainsKey(readMid))
            {
                mid = string.Empty;
                return false;
            }

            mid = _ssrcToMid.GetOrAdd(ssrc, readMid);
            return true;
        }

        // 3. Payload type unambiguously owned by one m-line.
        if (_payloadTypeToMid.TryGetValue(payloadType, out var ptMid))
        {
            mid = _ssrcToMid.GetOrAdd(ssrc, ptMid);
            return true;
        }

        mid = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves the MID for an already-associated <paramref name="ssrc"/> only — the SSRC-keyed path for
    /// packets that carry no MID/PT demux keys (e.g. RTCP, whose sender SSRC was learned from its RTP).
    /// Returns <see langword="false"/> when the SSRC has not been associated yet.
    /// </summary>
    public bool TryResolveBySsrc(uint ssrc, out string mid)
    {
        if (_ssrcToMid.TryGetValue(ssrc, out var associated))
        {
            mid = associated;
            return true;
        }

        mid = string.Empty;
        return false;
    }
}
