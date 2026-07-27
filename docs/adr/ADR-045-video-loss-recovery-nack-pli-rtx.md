# ADR-045: Video Loss Recovery — Gated NACK/PLI Feedback and RTX

Status: Accepted
Date: 2026-07-14

## Context

Video loss recovery has two halves over the video RTCP-mux channel: the **receiver** signals loss
(a Generic NACK naming the lost sequence numbers, RFC 4585 §6.2.1, plus a PLI keyframe fallback,
RFC 4585 §6.3.1) and the **sender** answers a NACK by retransmitting the packets on an RTX repair
stream (RFC 4588). Inbound PLI/FIR is surfaced to the application as a keyframe request. This ADR
covers the feedback + retransmit behaviour; the SDP that gates it is ADR-C12-01, the reorder
window that consumes recovered packets is ADR-C12-02, and the RTX repair-stream keying is
ADR-C12-04.

### Verified current state (graphify-grounded)

- `VideoKeyFrameFeedback` (`src/Core/Infrastructure/Rtp/VideoKeyFrameFeedback.cs`) runs both
  directions on the single-stream video RTCP channel. `OnRtcpPackets` (L64-98): any PLI/FIR →
  `KeyFrameRequested` callback (for the encoder); a Generic NACK → `_onRetransmitRequested`.
  `OnLoss` (L108-125): sends a Generic NACK **only if `_remoteSupportsNack` and the missing set is
  non-empty**, plus a throttled PLI **only if `_remoteSupportsPli`** — feedback the peer did not
  advertise in `a=rtcp-fb` is never sent (RFC 4585 §3). PLI is throttled to 1/500 ms
  (`PliThrottleTicks`, L19). NACK bitmask grouping (`BuildNackEntries`, L155-178) is RFC 4585
  §6.2.1-exact (bit i = PID + i + 1). PLI/FIR sent SRTCP-protected via `SendControlAsync`,
  suppressed before DTLS.
- Loss detection is **arrival-order and reorder-safe**: `VideoArrivalLossTracker.Track`
  (`src/Core/Infrastructure/Rtp/VideoArrivalLossTracker.cs` L34) / `LossReport` (L61) is tri-state:
  `null` for in-order/duplicate/reorder (no signal), empty for a forward gap > 256 (PLI only), a
  list for a small forward gap [2..256] (NACK + PLI). The highest-seen reference advances only on
  forward progress (L48-50) so a single reorder costs at most one signal, not a spurious cascade.
- Sender RTX: `VideoRtpStream.OnRetransmitRequested` (L275-289) pulls each still-buffered original
  from `RtpRetransmissionBuffer`, re-wraps it with the rtx PT/SSRC and a fresh rtx sequence
  (`RtxPacketFactory.Encapsulate`), and sends on the secondary stream. Sent packets are retained
  via `_rtp.PacketSent += _retransmitBuffer.Store` (L182). The rtx SSRC is full-range random
  (`RtpRandom.NextSsrc`, L179) — not a guessable 31-bit value.
- Receiver RTX: inbound repair packets (`SecondaryPacketReceived` → `OnRtxPacketReceived`,
  L419-434) are decapsulated and fed into the reorder window.

## Decision

1. **Feedback is strictly gated on the peer's advertised `a=rtcp-fb`.** NACK only if the peer
   offered `nack`; PLI only if it offered `nack pli`. A peer with no `a=rtcp-fb` gets no loss
   feedback.
2. **Loss classification is tri-state and reorder-safe.** In-order/duplicate/reorder → nothing;
   small forward gap → NACK the enumerated sequences + PLI; large forward gap (> 256) → PLI only
   (enumerating is pointless, a keyframe recovers faster). This is what suppresses the spurious
   NACK+PLI storm every reorder used to trigger.
3. **Signal loss on arrival, recover in order.** Loss is detected arrival-order (fast NACK, before
   the reorder window slides past the gap); ordered delivery and depacketiser reset are downstream.
4. **RTX send path is closed; the recovered packet re-enters the reorder window on receive.** The
   sender retransmits on the repair stream; the receiver decapsulates into the reorder buffer.

### Crux

Detecting loss on **arrival** (not on release from the reorder window) is deliberate: it fires the
NACK as early as possible, before the window can slide past the gap, at the cost of an occasional
spurious NACK on a reorder — harmless, since the duplicated RTX is dropped downstream as a
duplicate/too-late. The reorder-safe advance of the highest-seen reference is the mechanism that
keeps one reorder from being read as a fresh forward gap on every following in-order packet.

## Consequences

Positive: the recovery loop is RFC-correct and bandwidth-honest (no spurious feedback storms);
an RTX-capable, jitter-buffering peer gets genuine packet recovery.

Divergence / honesty:
- **No RFC 4585 NACK timing (RTCP bandwidth share).** NACKs are sent per-gap, ungoverned by the
  RTCP interval — noted follow-up.
- **FIR is honoured on receive but never generated** (`VideoKeyFrameFeedback` class doc L15).
- A **forward loss ≥ half the sequence space is classified as reorder** and not detected
  (`VideoArrivalLossTracker.LossReport` doc L55-60) — pathological, documented.
- A peer with **no `a=rtcp-fb` gets no feedback** — a behaviour change from the earlier
  unconditional PLI, but not a regression against SDK↔SDK or standard WebRTC peers (our offer
  always carries `nack` + `nack pli`).

## Guardrails

- Feedback gated on peer capabilities; fail-closed identically to the PLI path (no plaintext RTCP
  before DTLS/SRTCP).
- NACK bitmask stays RFC 4585 §6.2.1-symmetric (bit i = PID + i + 1); missing list must be
  ascending (`OnLoss` contract, L104-106).
- Loss classification stays reorder-safe (highest-seen advances only forward).
- rtx SSRC full-range random; RTX runs on the receive-loop thread (single consumer, no extra lock).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-video-pli-feedback.md`,
  `…-video-nack-gating.md`, `…-video-reorder-loss-signalling.md`,
  `…-video-rtx-retransmit.md`, `…-video-rtx-receive.md`.
- Code (graphify-verified): `src/Core/Infrastructure/Rtp/VideoKeyFrameFeedback.cs`
  (`OnRtcpPackets` L64, `OnLoss` L108, `RequestThrottledPli` L127, `BuildNackEntries` L155);
  `src/Core/Infrastructure/Rtp/VideoArrivalLossTracker.cs` (`Track` L34, `LossReport` L61);
  `src/Core/Infrastructure/Rtp/VideoRtpStream.cs` (`OnRetransmitRequested` L275,
  `OnRtxPacketReceived` L419, retransmit buffer wiring L174-187);
  `src/Core/Infrastructure/Rtp/Retransmission/RtxPacketFactory.cs`, `RtpRetransmissionBuffer.cs`.
- RFC: 4585 §3 (mode negotiation), §6.2.1 (Generic NACK), §6.3.1 (PLI); 4588 (RTX);
  5104 §4.3.1 (FIR); 3550 §8.1 (SSRC randomness).
