# Architecture Decision Records (ADR)

This directory is the canonical record of the load-bearing architecture decisions behind
CalloraVoipSdk. Each ADR captures one decision as **Context → Decision → Consequences →
Guardrails** (see `ADR-011` for the format model).

## Reading notes

- **Numbering is immutable.** ADR-001…012 are the original, hand-authored records. ADR-013…061
  were **backfilled on 2026-07-27** from the project's development history (114 archived
  engineering logs under `docs/archive/agent-log/`, git history, and the memory anchors) and
  **verified against the actual source** via the code graph. Where a log claimed more than the
  code delivers, the ADR records the real state and flags the divergence honestly — no ADR claims
  more than the technical evidence supports.
- **Status.** `Accepted` = decision taken and in effect (open limitations, if any, are stated in
  the ADR itself). `Proposed` = decided in principle, not fully realised in the tree.
- **Errata.** ADR-006 (§4 API-surface gate) and ADR-011 (B4/B5 slice wording) carry dated errata
  where the original prose diverged from the shipped code; the errata state the verified reality.
- Two decisions were verified as *already covered* by existing ADRs and deliberately not
  duplicated: BUNDLE/multi-track (ADR-010/011) and the WebRTC peer facade (ADR-009/012).
- **Type names are time-bound.** ADRs written before 4.6.0 name the old SIP-facade types. In 4.6.0
  they were renamed: `SdkConfiguration` → `VoipConfiguration`, `SdkOptions` → `VoipOptions`,
  `AddCallora(...)` → `AddCalloraVoip(...)` (no compatibility aliases). The ADR texts stay unchanged
  as a dated record — translate as you read. The current state is in
  [`CHANGELOG.md`](../../CHANGELOG.md).

### Architecture, Layering & Delivery Process

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-013](ADR-013-role-based-delivery-workflow.md) | Role-Based Delivery Workflow (CEO / PO / DEV / Reviewer) | Accepted | 2026-04-14 |
| [ADR-014](ADR-014-ddd-layering-gated-baselines.md) | DDD Layer Direction Enforced by Gated Shrink-Only Baselines | Accepted | 2026-07-08 |
| [ADR-015](ADR-015-icallregistry-domain-port-dip.md) | PhoneLine↔CallManager Decoupling via Domain Port `ICallRegistry` (DIP) | Accepted | 2026-07-08 |
| [ADR-016](ADR-016-peer-calibrated-refactoring-backlog.md) | Peer-Calibrated Refactoring Backlog — Harden, Don't Rebuild | Accepted | 2026-07-17 |
| [ADR-057](ADR-057-audit-findings-register-marker-discipline.md) | Audit → Findings-Register → Code-Marker as Durable, Claim-Verified Audit Memory | Accepted | 2026-07-22 |
| [ADR-058](ADR-058-layered-test-interop-soak-model.md) | Layered L0–L4 Test Model with Interop/Soak Harness and a Document-Don't-Fix Register | Accepted | 2026-07-21 |

### Product, Platform, Versioning & Extensibility

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-006](ADR-006-api-versioning-strategy.md) | API Versioning and Compatibility Strategy | Accepted | 2026-04-14 |
| [ADR-007](ADR-007-host-centric-platform-split.md) | Host-Centric Platform Split (Engine OSS + Host + Plugins) | Accepted | 2026-04-16 |
| [ADR-008](ADR-008-community-module-store.md) | Community Module Store Architecture | Proposed | 2026-07-11 |
| [ADR-059](ADR-059-public-media-tap-contract.md) | Public Per-Call Media-Tap Contract (Synchronous, Encoded, Fan-Out) | Accepted | 2026-07-07 |

### SIP Signaling

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-001](ADR-001-uas-user-identity-policy.md) | UAS User Identity Policy (RFC 3261 §8.2.2.1) | Accepted | 2026-04-09 |
| [ADR-002](ADR-002-uas-redirect-redirect-async.md) | UAS Redirect (RFC 3261 §8.3) — RedirectAsync on ISipCallSession | Accepted | 2026-04-09 |
| [ADR-003](ADR-003-rfc3581-rport-uas-response-reflection.md) | RFC 3581 §4 – rport/received Reflection in UAS Responses | Accepted | 2026-04-09 |
| [ADR-004](ADR-004-rfc5626-crlf-keepalive-pong.md) | RFC 5626 §4.4.1 – CRLF Keepalive Pong on Stream Transports | Accepted | 2026-04-09 |
| [ADR-005](ADR-005-rfc3261-cancel-gate-release.md) | RFC 3261 §9.1 – Release Operation Gate Before INVITE Transaction | Accepted | 2026-04-09 |
| [ADR-017](ADR-017-register-expires-lifecycle.md) | REGISTER Expires / Refresh Lifecycle | Accepted | 2026-07-09 |
| [ADR-018](ADR-018-challenge-driven-digest-auth.md) | Challenge-Driven Digest Authentication for REGISTER | Accepted | 2026-07-09 |
| [ADR-019](ADR-019-nat-routable-contact-and-media-address.md) | NAT-Routable Contact and Advertised Media Address | Accepted | 2026-07-08 |
| [ADR-020](ADR-020-dialog-route-set-record-route.md) | Dialog Route-Set — Record-Route Echo and In-Dialog Routing | Accepted | 2026-07-09 |
| [ADR-021](ADR-021-trunk-inbound-matching.md) | Trunk-Inbound Line Matching | Accepted | 2026-07-08 |
| [ADR-022](ADR-022-invite-transaction-robustness.md) | INVITE Transaction Robustness — Retransmission, Fork, 100rel, Codec | Accepted | 2026-07-09 |
| [ADR-023](ADR-023-session-timer-negotiation.md) | RFC 4028 Session-Timer Negotiation | Accepted | 2026-07-09 |

### SDP Offer/Answer

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-024](ADR-024-sdp-offer-answer-origin-and-extmap-negotiation.md) | SDP Offer/Answer Negotiation — Origin Versioning and extmap Id Assignment | Accepted | 2026-07-14 |

### Media Security — SDES / SRTP

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-025](ADR-025-sdes-offer-answer-negotiation.md) | SDES Offer/Answer Negotiation with Own-Key Answers and Fail-Closed Keyless Rejection | Accepted | 2026-07-09 |
| [ADR-026](ADR-026-srtp-media-path-fail-closed-hardening.md) | SRTP Media-Path Wiring — Fail-Closed Keying, Wire-Derived Keys, and Hardening | Accepted | 2026-07-09 |
| [ADR-027](ADR-027-srtp-continuity-reinvite-rekey.md) | SRTP Continuity Across Re-INVITE — Hold/Unhold Key Stability and Peer Rekey | Accepted | 2026-07-09 |

### Media Security — DTLS-SRTP

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-028](ADR-028-dtls-srtp-foundation.md) | DTLS-SRTP Keying Foundation (RFC 5763/5764) | Accepted | 2026-07-14 |
| [ADR-029](ADR-029-dtls-srtp-signaling-and-keying-precedence.md) | DTLS-SRTP Signaling, Answer-Role, and Keying-Method Precedence | Accepted | 2026-07-14 |
| [ADR-030](ADR-030-dtls-srtp-media-wiring.md) | DTLS-SRTP Media Wiring and Fail-Closed Context Installation | Accepted | 2026-07-14 |
| [ADR-066](ADR-066-dtls-post-handshake-association-servicing.md) | DTLS Post-Handshake Association Servicing and Egress Ordering | Accepted | 2026-08-06 |
| [ADR-067](ADR-067-dtls-stateless-cookie-scope.md) | Scope of the DTLS Stateless Cookie (HelloVerifyRequest) | Accepted | 2026-08-12 |

### Media Security — SRTCP

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-031](ADR-031-srtcp-crypto-core-and-rtcp-path-wiring.md) | SRTCP Crypto Core and RTCP-Path Wiring | Accepted | 2026-07-09 |

### RTP Transport & Wire-Boundary Hardening

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-032](ADR-032-media-hotpath-allocation-avoidance.md) | Media Hot-Path and SIP-Framer Allocation Avoidance | Accepted | 2026-07-09 |
| [ADR-033](ADR-033-wire-boundary-dos-hardening.md) | DoS Hardening at the RTP and SIP Wire Boundaries | Accepted | 2026-07-09 |
| [ADR-034](ADR-034-secondary-stream-and-onebyte-header-extensions.md) | Secondary-Stream Transport and RFC 8285 One-Byte Header Extensions | Accepted | 2026-07-14 |
| [ADR-035](ADR-035-rtx-retransmission-mechanics.md) | RTX Retransmission Mechanics — OSN Encapsulation and the Retransmit Buffer | Accepted | 2026-07-14 |

### RTCP, Feedback, Congestion Control & Timing

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-036](ADR-036-rtcp-wire-codec-tolerant-decode.md) | RTCP Wire Codec — Feedback/XR Framing and Tolerant Compound Decode | Accepted | 2026-07-15 |
| [ADR-037](ADR-037-rtt-convergence-seed.md) | Jitter-Buffer RTT Convergence Seed | Accepted | 2026-07-09 |
| [ADR-038](ADR-038-transport-cc-feedback-plane.md) | Transport-Wide Congestion Control — Feedback Plane (seq stamping, arrival recording, RTCP feedback) | Accepted | 2026-07-15 |
| [ADR-039](ADR-039-transport-cc-estimator-bitrate-api.md) | Transport-Wide Congestion Control — Estimator, AIMD Rate Policy, and Recommended-Bitrate API | Accepted | 2026-07-15 |
| [ADR-061](ADR-061-monotonic-media-clock.md) | Monotonic Clock for Time-Based Media and RTCP Computations | Accepted | 2026-07-24 |

### ICE Connectivity (SIP path)

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-040](ADR-040-send-side-ice-state-machine.md) | Send-Side ICE State Machine on the SIP Path (RFC 8445) | Accepted | 2026-07-09 |
| [ADR-041](ADR-041-consent-freshness-and-ice-restart-primitives.md) | ICE Consent-Freshness and Restart as Built-but-Unwired SIP Primitives | Accepted | 2026-07-09 |
| [ADR-042](ADR-042-ice-verification-and-shared-socket-gathering.md) | ICE Verification Discipline and Shared-Socket srflx Gathering | Accepted | 2026-07-08 |

### Video Media Path

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-043](ADR-043-video-sdp-negotiation.md) | Video SDP Offer/Answer Negotiation (m=video, rtcp-fb, RTX/apt) | Accepted | 2026-07-14 |
| [ADR-044](ADR-044-video-packetisation-and-reorder-playout.md) | Video Packetisation and Contiguous-Release Reorder Playout | Accepted | 2026-07-14 |
| [ADR-045](ADR-045-video-loss-recovery-nack-pli-rtx.md) | Video Loss Recovery — Gated NACK/PLI Feedback and RTX | Accepted | 2026-07-14 |
| [ADR-046](ADR-046-video-media-security-sdes-dtls.md) | Video Media Security — Per-m-line SDES, RTX Keying, DTLS Precedence | Accepted | 2026-07-14 |
| [ADR-047](ADR-047-video-ice-media-layer-and-candidates.md) | Video ICE — Per-5-tuple Media Layer, Shared Credentials, Full Candidate Gathering | Accepted | 2026-07-14 |
| [ADR-048](ADR-048-video-media-stream-and-channel-activation.md) | Video Media Stream and SIP Channel Activation | Accepted | 2026-07-14 |
| [ADR-068](ADR-068-opaque-video-payload-format.md) | Opaque Video Payload Format for End-to-End Encrypted Frames | Accepted | 2026-08-17 |

### Media Supervision

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-069](ADR-069-media-supervision-liveness-not-silence.md) | Media Silence Is Reported, Not Terminated On | Accepted | 2026-08-17 |

### Audio Codecs

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-049](ADR-049-opus-codec-integration-concentus.md) | Opus Audio Codec Integration via Concentus (Managed Encode/Decode) | Accepted | 2026-07-08 |
| [ADR-050](ADR-050-bridge-audio-transcoding-opus-mulaw.md) | Bridge Audio Transcoding — Wire Codec ↔ µ-law Tap | Accepted | 2026-07-08 |

### Concurrency & QoS

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-051](ADR-051-event-dispatch-and-object-lifecycle-concurrency.md) | Event-Dispatch and Object-Lifecycle Concurrency Contract | Accepted | 2026-07-08 |
| [ADR-052](ADR-052-media-hotpath-lockfree-fanout-and-bounded-buffers.md) | Media Hot-Path Concurrency — Lock-Free Fan-Out and Bounded Drop-Oldest Buffers | Accepted | 2026-07-08 |
| [ADR-053](ADR-053-qos-observed-not-marked.md) | QoS as an Observed Metric, Not a Marked Packet | Accepted | 2026-07-08 |

### BUNDLE & Multi-Track Transport

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-010](ADR-010-bundle-transport-slice-plan.md) | BUNDLE Media-Transport — Slice-Plan | Accepted | 2026-07-15 |
| [ADR-011](ADR-011-rtp-multitrack-transport.md) | Multi-Track RTP Transport for BUNDLE | Accepted | 2026-07-15 |

### WebRTC & Browser Interop

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-009](ADR-009-webrtc-browser-peer-roadmap.md) | WebRTC Browser-Peer Roadmap | Accepted | 2026-07-15 |
| [ADR-012](ADR-012-webrtc-public-facade.md) | WebRTC Public Facade (`WebRtcClient`) | Accepted | 2026-07-18 |
| [ADR-060](ADR-060-webrtc-facade-completion-and-server-hosting.md) | WebRTC Facade Completion — Fluent ICE Config, Send-Side Simulcast, and the TURN/STUN Server-Hosting Facade | Accepted | 2026-07-19 |

### TURN Relay

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [ADR-054](ADR-054-turn-relay-as-ice-candidate.md) | TURN Relay as a First-Class ICE Candidate | Accepted | 2026-07-19 |
| [ADR-055](ADR-055-turn-control-stack-allocation-permission-keepalive.md) | TURN Control Stack — Allocation, Permission, and Keepalive over the Shared Socket | Accepted | 2026-07-19 |
| [ADR-056](ADR-056-post-nomination-whole-socket-relay-transition.md) | Post-Nomination Whole-Socket Relay Transition (ChannelBind / ChannelData) | Accepted | 2026-07-19 |
