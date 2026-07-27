# ADR-035: RTX Retransmission Mechanics — OSN Encapsulation and the Retransmit Buffer

Status: Accepted
Date: 2026-07-14

## Context

NACK-driven loss recovery over RTP (RFC 4585 → RFC 4588) needs two codec-agnostic
building blocks below the media-specific wiring: a way to **re-wrap** an already-sent
packet as a retransmission on a repair stream, and a bounded **history** of recently sent
packets so a NACK can find the target to re-wrap. These are the mechanics of RTX — distinct
from the transport channel they ride on and from the video feedback loop that drives them.
They were built as an isolated, independently testable slice, before any session wiring, so
the format and the buffer could be pinned byte-exact on their own.

This ADR captures **only** the generic RTX mechanics. It deliberately does **not** re-decide:

- the **secondary-stream transport** (a second SSRC + its own SRTP context on the shared
  socket) — that is ADR-C07-03, which owns the channel and explicitly disclaims RTX
  semantics; and
- the **video loss-recovery loop** (gated NACK/PLI, `VideoRtpStream` send/receive wiring,
  reorder-window re-entry) — that is ADR-C12-03, which consumes these primitives as given.

The mechanics sit in the seam between those two: C07-03 provides the pipe, C12-03 provides
the video policy, and this ADR provides the RFC 4588 §4 packet format and the retransmit
buffer that both rely on but neither defines. The building blocks are codec-agnostic by
construction (they operate on a plain `RtpPacket` and live under `Rtp/Retransmission/`, not
under any video type), so the same mechanics are available to an audio RTX path if one is
ever wired — nothing here is video-specific.

Governing rules: ENGINEERING_RULES **K3** (thread-safety by design, bounded buffers on the
media path, drop-oldest over unbounded growth), **K7** (RFC references in code), and the
RFC 3711 §3.2 per-SSRC cryptographic-context invariant that motivates the separate stream.

### Verified current state (graphify + code)

- **`RtxPacketFactory`** (`src/Core/Infrastructure/Rtp/Retransmission/RtxPacketFactory.cs`,
  `internal static`, graphify community 33). `Encapsulate(original, rtxPayloadType, rtxSsrc,
  rtxSequenceNumber)` (L27) allocates `2 + payload.Length` bytes, writes the original 16-bit
  sequence number big-endian as the OSN prefix (`BinaryPrimitives.WriteUInt16BigEndian`,
  L33), copies the original payload after it, and stamps the rtx PT/SSRC/seq while copying
  the original's `Timestamp` and `Marker` (RFC 4588 §4). `TryDecapsulate(rtx,
  originalPayloadType, originalSsrc, out original)` (L54) rejects a payload shorter than the
  2-byte OSN (`return false`, L60-61), else reads the OSN back into the sequence number and
  restores the caller-supplied original PT/SSRC. Both methods carry a `DECISION` doc block
  (L18-25, L47-53): the original's **header extensions and CSRC list are intentionally not
  carried across** — RFC 4588 §4 defines the RTX payload as OSN + original payload only, and
  a repair packet's own extensions (abs-send-time, transport-cc) differ from the original's.
- **`RtpRetransmissionBuffer`**
  (`src/Core/Infrastructure/Rtp/Retransmission/RtpRetransmissionBuffer.cs`, `internal
  sealed`). A bounded seq→`RtpPacket` history: a `Dictionary<ushort, RtpPacket>` plus a
  `Queue<ushort>` eviction order under a single `_sync` lock. `Store` (L39): a new sequence
  enqueues and, when at capacity, evicts the oldest first; a **resent sequence replaces the
  stored entry without re-enqueuing** (guarded by `ContainsKey`, L45) so the queue and
  dictionary never diverge and the window never grows past capacity. `TryGet` (L61) and
  `Count` (L70) read under the same lock. Capacity defaults to 512 and is **capped at 32768**
  (ctor `ThrowIfGreaterThan`, L28) with the rationale in the ctor doc (L19-24): staying well
  below 65536 guarantees 16-bit sequence-number wrap cannot alias a live entry with an
  evicted one.
- **Tests** (`tests/CalloraVoipSdk.Core.IntegrationTests/RtxRetransmissionTests.cs`, community
  33): `Encapsulate_prefixes_osn_and_rewrites_stream_identity` (L15),
  `Encapsulate_then_decapsulate_reproduces_the_original` (L31),
  `Encapsulation_intentionally_drops_header_extensions_and_csrc` (L48),
  `Decapsulate_of_a_zero_length_original_payload_is_supported` (L84),
  `Buffer_returns_a_stored_packet_by_sequence_number` (L98),
  `Buffer_evicts_the_oldest_packet_beyond_capacity` (L111),
  `Buffer_resend_of_same_sequence_replaces_without_growing` (L125),
  `Buffer_is_safe_under_concurrent_store_and_lookup` (L137).
- **Consumers (out of scope here, named for the boundary):** `VideoRtpStream`
  (`OnRetransmitRequested` L275 uses `Encapsulate`; `_rtp.PacketSent += buffer.Store` wiring)
  drives these primitives; `VideoCodecCatalog.NegotiateRtx/BuildRtx/TryReadApt` negotiates the
  `a=rtpmap …/rtx` + `a=fmtp … apt=` binding. Both are decided in ADR-C12-03 / the video SDP
  ADR, not here.

## Decision

Ship RTX as two codec-agnostic mechanics, isolated from both the transport and the video
policy:

1. **OSN encapsulation format (`RtxPacketFactory`, RFC 4588 §4).** A retransmission is the
   original payload prefixed with the original 16-bit sequence number (big-endian, 2 bytes),
   re-stamped with the repair stream's payload type, SSRC, and a fresh sequence number, and
   carrying the original's timestamp and marker. Decapsulation strips the OSN and restores the
   caller-supplied original PT/SSRC; a payload shorter than the OSN fails closed
   (`false`/`null`).
2. **Header extensions and CSRC are intentionally not carried.** The RTX payload is OSN +
   original payload only (RFC 4588 §4). The recovered packet has no extensions/CSRC; a repair
   packet's own extensions (abs-send-time, transport-cc) are stamped by the send path, not
   inherited from the original. This is pinned by a dedicated test, not left implicit.
3. **Bounded, wrap-safe retransmit buffer (`RtpRetransmissionBuffer`).** A capacity-limited
   seq→packet history with drop-oldest eviction, resent-sequence replace-in-place (no regrow,
   no queue/dictionary divergence), and a hard 32768 cap so 16-bit wrap cannot alias a live
   entry with an evicted one. Thread-safe by a single lock: the send path stores, the RTCP
   receive path looks up, on different threads.

**Crux:** RTX must run on its **own** SSRC and sequence space, not by replaying the original
over the primary SRTP context. A plain resend would be rejected by the primary stream's
SRTP replay window (RFC 3711 §3.2 keys the cryptographic context — ROC + replay — per SSRC).
Giving the repair packet a distinct SSRC gives it a distinct replay context, so the resend
is accepted rather than dropped as a replay. The OSN prefix is what lets the receiver map the
repair packet back to the original sequence number the NACK named. The buffer's 32768 cap is
the second load-bearing invariant: it keeps 16-bit sequence-number arithmetic unambiguous.

## Consequences

Positive: the RTX format and buffer are byte-exact and concurrency-tested in isolation, so
the later video wiring (ADR-C12-03) could compose them without re-verifying the format, and
an audio RTX path could reuse the identical primitives unchanged. The separate-SSRC design
means the audio-critical primary SRTP path is never touched by retransmission traffic.

Divergence / honesty:
- **No RTT-scaled buffer sizing.** Capacity is a fixed count (default 512, "roughly a
  round-trip worth of packets, baresip keeps ~500 ms"), not derived from a measured RTT — a
  NACK for a packet older than the window simply misses. Noted, not adaptive.
- **These mechanics transmit nothing on their own.** As the founding log states, "es
  überträgt noch nichts neu" — no session wiring, no NACK receipt, no RTX SSRC/PT management,
  no receive-side decapsulation are in this slice. Those are ADR-C12-03 (video) and would be
  a separate decision for any future audio RTX. No DONE / compliance claim.
- **No RFC 4588 §8 SDP (`a=rtpmap …/rtx`, `a=fmtp apt=`) here.** The apt binding is negotiated
  in `VideoCodecCatalog` and belongs to the video SDP ADR; this slice assumes the rtx PT/SSRC
  are already chosen and passed in.
- **No external-stack interop claim.** Evidence is in-process unit tests over the packet types.

## Guardrails

- Encapsulate/Decapsulate stay RFC 4588 §4 byte-exact (OSN = big-endian original sequence
  number, 2 bytes, prefixed to the original payload); the header-extension/CSRC drop is a
  DECISION on both methods and pinned by a test — it must not silently start carrying them.
- `TryDecapsulate` fails closed (`false`/`null`) on a payload shorter than the OSN.
- `RtpRetransmissionBuffer` stays bounded and drop-oldest; capacity capped at 32768 so
  sequence-number wrap cannot alias a live entry with an evicted one; a resent sequence
  replaces in place and is not re-enqueued (queue/dictionary invariant).
- The buffer is the only shared state and is fully lock-guarded (store on the send thread,
  lookup on the RTCP-receive thread) — ENGINEERING_RULES K3.
- Retransmission MUST ride a separate SSRC (own SRTP replay/ROC context, RFC 3711 §3.2) — the
  reason a plain resend over the primary context is rejected; the transport that provides that
  separate context is ADR-C07-03, not re-decided here.

## Sources

- Log: `docs/archive/agent-log/2026-07-14-dev-rtx-mechanics.md` (RTX-Retransmit-Mechanik:
  OSN encapsulation + retransmit buffer, isolated mechanics, 9 tests, SRTP-replay rationale,
  MAJOR-1 header-extension/CSRC-drop → DECISION + pinning test).
- Code (graphify-verified, community 33): `RtxPacketFactory.cs`
  (`Encapsulate` L27, `TryDecapsulate` L54, DECISION L18-25/L47-53),
  `RtpRetransmissionBuffer.cs` (`Store` L39, `TryGet` L61, capacity cap L28, ctor rationale
  L19-24); tests `RtxRetransmissionTests.cs` (OSN layout L15, roundtrip L31,
  extension/CSRC drop L48, zero-length L84, buffer lookup/evict/replace/concurrency L98-137).
- Boundary (decided elsewhere, not here): ADR-C07-03 (secondary-stream transport + separate
  SRTP context), ADR-C12-03 (gated NACK/PLI + `VideoRtpStream` RTX wiring + reorder re-entry),
  video SDP ADR (`VideoCodecCatalog` rtx/apt negotiation, RFC 4588 §8.1).
- RFC: 4588 §4 (RTX packet format / OSN), §9 (separate SSRC/sequence space); 4585 §6.2.1
  (Generic NACK that drives retransmission); 3711 §3.2 (per-SSRC cryptographic context —
  ROC + replay); ENGINEERING_RULES K3 (bounded buffers / thread-safety), K7 (RFC refs).
