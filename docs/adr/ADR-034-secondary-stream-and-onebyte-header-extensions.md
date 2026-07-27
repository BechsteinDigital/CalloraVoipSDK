# ADR-034: Secondary-Stream Transport and RFC 8285 One-Byte Header Extensions

Status: Accepted
Date: 2026-07-14

## Context

Two transport-layer foundations had to exist before higher-level media features (RTX
retransmission per RFC 4588, and congestion control / BWE per transport-cc / RFC 8888) could be
built. Both are additive to the proven single-stream `RtpSession`, and both were deliberately
shipped as isolated slices — the risky infrastructure decoupled from the semantics that ride on
it — so the central receive/send path stays byte-identical for every existing audio call.

1. **A secondary stream on the same socket.** RTX retransmits lost packets on a *separate*
   SSRC and sequence space (RFC 4588 §9). Feeding those retransmissions through the primary
   SRTP context would corrupt the primary replay window / ROC. The transport therefore needs a
   second payload type carried on the same 5-tuple with its *own* SRTP context — before any RTX
   semantics exist.
2. **Element-level header extensions.** The RTP layer only carried the generic RFC 3550
   extension (`RtpExtension` = profile `0xBEDE` + one opaque padded blob) — no element layer. The
   RFC 8285 one-byte form packs multiple `{id, value}` elements into that `0xBEDE` block; it is
   the carrier for the transport-wide sequence number that congestion control reports arrival
   times against.

The governing rules are ENGINEERING_RULES **K3** (hot-path allocation hygiene, thread-safety
by design), **K1** (fail-closed media security), and **K7** (RFC references in code).

### Verified current state (graphify + code)

- **`RtpSession` hosts a secondary stream with its own SRTP context.**
  (`src/Core/Infrastructure/Rtp/Session/RtpSession.cs`.) Fields: `_secondaryPayloadType`
  (`volatile int`, `-1` = off, L67), `_secondaryOutboundSrtp` / `_secondaryInboundSrtp`
  (`ISrtpContext?`, Volatile-accessed, L68-69). API: `event SecondaryPacketReceived` (L129),
  `ConfigureSecondaryStream(byte)` (L388), `InstallSecondarySecurityContexts(out, in)` (L399),
  `SecondaryPayloadType` getter (L391), `SendSecondaryAsync(RtpPacket, ct)` (L413).
- **Inbound intercepts the secondary PT *before* the primary SRTP block.** In `ProcessDatagram`
  (L628-634) the demux order is STUN → DTLS → RTCP → secondary → primary: when
  `_secondaryPayloadType >= 0` and the plaintext RTP header PT (`datagram[1] & 0x7F`) matches,
  the datagram routes to `ProcessSecondaryDatagram` (L798). The PT read is safe pre-decrypt
  because the RTP header is plaintext under SRTP. The RTCP mask is disjoint from any RTP PT, so
  there is no collision with the RTCP arm above it.
- **The secondary path is fail-closed and does not disturb the primary.**
  `ProcessSecondaryDatagram` decrypts with `_secondaryInboundSrtp` under the same three-way drop
  as the primary (auth / replay / `ArgumentException|CryptographicException|ObjectDisposedException`,
  L806-827; see ADR C07-02), then fires `SecondaryPacketReceived`. It deliberately **skips** the
  symmetric-RTP latch and SSRC validation (comment L793-797): the secondary rides the
  already-latched 5-tuple and its sequence space is validated by the consumer via the recovered
  original packet. `SendSecondaryAsync` (L413) protects with `_secondaryOutboundSrtp` under a
  dedicated `_secondarySrtpProtectSync` lock, suppresses the send fail-closed when
  `RequireEncryptedMedia` and no context is installed (L433-437, ENGINEERING_RULES K1), and omits
  the SR counters (its own SSRC). `SecondaryPacketReceived` is nulled on dispose (L883).
- **The RFC 8285 one-byte codec exists and is fully wired, past what the founding log described.**
  `OneByteRtpHeaderExtensions` (`src/Core/Infrastructure/Rtp/Packets/OneByteRtpHeaderExtensions.cs`,
  graphify community 87) provides `Encode` (fail-fast `ArgumentException` on id∉1..14 or
  value-length∉1..16, L34), `Parse` (lenient on receive: skip padding, stop at id 15, drop a
  truncated trailing element; **each value copied** because the source may alias the pooled
  receive buffer, L104). Beyond the original wire-only slice it now also carries
  `TransportSequenceNumber` (L116), `EncodeTransportSequenceNumber` (L130, allocation-lean
  direct-bytes stamp for the per-packet path) and `TryReadTransportSequenceNumber` (L151,
  inline scan, no list/per-element copy, allocation-free on receive).
- **The codec drives real congestion control.** `RtpOutboundHeaderExtensionStamper`
  (`src/Core/Infrastructure/Rtp/Session/RtpOutboundHeaderExtensionStamper.cs`) builds the
  per-packet extension from transport-cc + (on BUNDLE) MID (RFC 9143) + (on simulcast) RID
  (RFC 8852), reusing `EncodeTransportSequenceNumber` on the non-BUNDLE path so it is
  byte-identical to stamping transport-cc alone (L77-80). Consumers include
  `TransportCcCongestionController`, `TransportCcFeedbackSender`, `RtpSession`, and
  `VideoRtpStream` (grep-confirmed).

## Decision

Ship the two foundations as additive, self-contained transport slices:

1. **Secondary-stream transport in `RtpSession`.** A second payload type on the same socket with
   its own outbound/inbound SRTP context (independent ROC + replay window, RFC 4588 §9),
   intercepted before the primary SRTP block, fail-closed identically to the primary, latch- and
   SSRC-validation-skipped by design. **No RTX semantics** in this slice — pure transport.
2. **RFC 8285 one-byte header-extension codec.** Fail-fast on construction, lenient on receive,
   value-copying to survive the pooled receive buffer; plus an allocation-lean transport-cc
   stamp/read pair for the per-packet path.

**Crux:** the secondary stream keeps a *separate* SRTP context so its independent sequence space
never advances the primary stream's replay window — that isolation is the entire point, and it is
what lets RTX be built later without touching the audio-critical primary path. The header-extension
codec is fail-fast when *we* build (a bad element is a construction bug) but lenient when a peer
sends (unknown/padding/truncation tolerated) — the asymmetry is deliberate.

## Consequences

Positive:
- The central receive path is byte-identical when `_secondaryPayloadType == -1` (every existing
  call), verified by the demux-order review and the regression suite. Six secondary-stream tests
  cover dispatch separation, an SRTP round-trip with the secondary's own context, **replay-window
  separation** (a secondary replay does not advance the primary window — the core purpose), an
  auth-fail drop that does not kill the loop, a send reaching the peer with the secondary SSRC,
  and a fail-closed send-suppress under `RequireEncryptedMedia` without a context.
- The one-byte codec has 15 round-trip/edge tests (ordering, 32-bit padding, inter-element
  padding skip, id-15 stop, non-`0xBEDE` → empty, truncation tolerance, 16-byte value,
  id-1/id-14 boundaries, rejection of id 0/15/16 and value length 0/17). A reviewer H1 finding —
  `Parse` aliasing `extension.Data` over a pooled buffer — was fixed with a per-value `.ToArray()`
  copy, closing the exact hot-path/pooling hazard C07-01 introduced.

Tradeoffs / honest divergence:
- **The founding logs understate the current state — recorded honestly.** The one-byte-extension
  log (2026-07-14) scoped itself as "reine Wire-Ebene, keine RtpSession-Verdrahtung … Fundament";
  the code has since grown the transport-cc stamp/read helpers and is wired through
  `RtpOutboundHeaderExtensionStamper` into live congestion control and `VideoRtpStream`. The
  secondary-stream log (2026-07-14) explicitly shipped "keine RTX-Semantik, keine Video-
  Verdrahtung"; whether RTX now rides it is a separate cluster's concern (WebRTC/RTX roadmap) and
  is **not** claimed here — this ADR captures the transport foundation only.
- **The secondary path skips the symmetric-RTP latch and SSRC/sequence validation by design.**
  That is correct only under the stated assumption (it rides the already-latched 5-tuple and the
  consumer validates the recovered original). If a secondary stream were ever used outside that
  assumption, the missing validation would be a gap — the assumption is load-bearing.
- **SDP `a=extmap` negotiation is not part of this codec slice.** The id↔URI mapping (RFC 8285 §5)
  that tells the peer which id carries transport-cc/MID/RID is negotiated elsewhere; the codec
  assumes an already-agreed id.
- No external-stack interop claim; evidence is in-process unit tests over real sockets.

## Guardrails

- The secondary inbound/outbound contexts are separate `ISrtpContext` instances with their own
  ROC + replay window; a secondary replay MUST NOT advance the primary window (pinned test).
- Secondary inbound applies the same fail-closed three-way drop as the primary; a fail never
  terminates the receive loop (ADR C07-02 parity).
- `SendSecondaryAsync` suppresses under `RequireEncryptedMedia` with no context — no plaintext
  leak (ENGINEERING_RULES K1); protect runs under `_secondarySrtpProtectSync`.
- Demux order STUN → DTLS → RTCP → secondary → primary; the secondary PT test reads only the
  plaintext RTP header byte and is skipped byte-identically when `_secondaryPayloadType == -1`.
- `OneByteRtpHeaderExtensions.Encode` fail-fast on id∉1..14 / length∉1..16; `Parse` /
  `TryReadTransportSequenceNumber` copy each value so a returned element never aliases the pooled
  receive buffer (ADR C07-01 hazard closed).
- `EncodeTransportSequenceNumber` / `TryReadTransportSequenceNumber` stay allocation-free on the
  per-packet path (ENGINEERING_RULES K3).

## Sources

- Logs: `docs/archive/agent-log/2026-07-14-dev-rtpsession-secondary-stream.md` (secondary
  transport, separate SRTP context, replay-window separation, 6 tests),
  `2026-07-14-dev-rtp-onebyte-header-extensions.md` (RFC 8285 one-byte codec, fail-fast/lenient,
  H1 aliasing fix, 15 tests).
- Code (graphify-verified): `RtpSession.cs` (secondary fields L67-69, API L129/L388/L399/L413,
  inbound intercept L628-634, `ProcessSecondaryDatagram` L798-848, dispose L883),
  `OneByteRtpHeaderExtensions.cs` (community 87: `Encode` L34, `Parse` value-copy L104,
  `EncodeTransportSequenceNumber` L130, `TryReadTransportSequenceNumber` L151),
  `RtpOutboundHeaderExtensionStamper.cs` (transport-cc + MID/RID build, non-BUNDLE byte-identical
  L77-80); wiring grep-confirmed in `TransportCcCongestionController.cs`,
  `TransportCcFeedbackSender.cs`, `VideoRtpStream.cs`.
- Markers / RFC: RFC 4588 §9 (RTX separate SSRC/sequence), RFC 8285 §5 (one-byte header
  extensions / extmap), RFC 8888 (transport-cc feedback), RFC 9143 (MID), RFC 8852 (RID);
  ENGINEERING_RULES K1 (fail-closed), K3 (hot-path alloc/thread-safety), K7 (RFC refs).
