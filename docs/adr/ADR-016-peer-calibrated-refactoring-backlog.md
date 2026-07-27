# ADR-016: Peer-Calibrated Refactoring Backlog — Harden, Don't Rebuild

Status: Accepted
Date: 2026-07-17

## Context

Before committing to a large structural refactor, the team ran a read-only architecture assessment
of the whole Core (six fan-out audits over SIP / RTP+Media / SRTP+STUN+TURN+DTLS / SDP / Client+SDK
/ Domain+Application), measured against three catalogs — Microsoft Framework Design Guidelines /
SOLID / Clean Architecture, GoF patterns, and the refactoring.guru/Fowler smell→technique catalog —
and **peer-calibrated** against SIPSorcery, PJSIP, baresip/libre, and Ozeki rather than applying the
catalog blindly. The sibling `callora` repo had used the same method and found an anemic-domain
problem; the question was whether `voip` had the same debt or a different shape.

### Verified current state

- **DDD layering is clean and gate-backed.** `LayeringBaseline = []` /
  `LayerSegmentBaseline = []` in `tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs`
  (L20/L63) — matching the assessment's "0 verifizierte Schichtverletzungen".
- **Rich Domain over peer median.** Aggregates with invariants exist:
  `Call`/`PhoneLine` (`src/Core/Domain/Calls/Call.cs`, `Core.Domain.Calls`), validated value objects
  (`SipAddress`/`CallId`/`DtmfTone`). So the callora-style "introduce value objects / rich
  aggregates" refactor is N/A here.
- **The named God-Class site is real and has since been decomposed.** `TurnServer`
  (`src/Core/Infrastructure/Turn/Server/TurnServer.cs` L26) now delegates to extracted collaborators
  — graphify shows `TurnAllocationRegistry`, `TurnAllocateRequestHandler`,
  `TurnServerResponseFactory`, `TurnPortReservationStore`, `TurnMobilityService`,
  `TurnTcpConnectionBroker`, `TurnTcpExtensionHandler` (R3 / HARD-G2 Extract-Class outcome), not a
  pure file-split.
- **The convention layer is codified.** `ENGINEERING_RULES.md` records the K-conventions
  (K1 fail-closed, K2 enricher order, K3 threading, K4 trust-boundary error handling, K5 secrets)
  and R1–R6 as the mechanical rules — the vocabulary the assessment says "the team already speaks".

## Decision

Treat the gap to world-class as **hardening + discipline, not a rebuild**, and drive it through one
merged, peer-calibrated backlog:

1. **Merge the refactoring.guru R-list with the existing Hardening Paket G** into a single
   prioritised order (R1=G1, R2=G3, R3⊆G2), so the two backlogs cannot diverge.
2. **Adopt only three tight clusters** as substantial work: (A) the `TurnServer` God-Class Extract
   Class; (B) genuine duplicate-code twins (crypto replay window SRTP≙SRTCP, SIP wire scanner, SDP
   keying ×3); (C) the already-noted G-items (namespace drift, silent-catch) which the audit confirms
   and broadens.
3. **Explicitly reject** catalog items that fight the peer norms or the zero-alloc/pass-through hot
   path: primitive-obsession wrapping of wire types (SSRC/PT/SeqNo), Visitor for wire codecs,
   State-pattern classes for the transaction/call FSM, config-type merge, and splitting long
   RFC-atomic wire/crypto/FSM methods. "Ziel ist nicht 23/23."
4. **Order by value/risk**: R1 (namespace + arch-test tighten) → R2 (silent-catch) → R6 (DRY batch)
   → R4 (crypto replay window) → R5 (`CallMediaParameters` record/`with`) → R7 (long param list) →
   R3/G2 (`TurnServer` split, biggest structural value, most invasive last).

### Crux design pieces

- **Peer calibration as the filter.** A smell in the Fowler catalog is only adopted if it also reads
  as debt against SIPSorcery/PJSIP/baresip/Ozeki; several catalog "findings" were actively refuted
  (primitive obsession, Visitor, State classes, long-method splits) because the peers do the same by
  design.
- **Merge, don't fork, the backlogs.** R and Paket G become one order — the assessment's guardrail
  against maintaining two overlapping debt lists.
- **Guardrail-gap discovery.** The assessment found the R2 namespace drift (`Core.Security` under
  `Domain/Security/`) was *invisible* to the then-current arch test (foreign-layer-only match), so
  the fix pairs a rename with tightening the gate to a full path match (HARD-G1 / R1) — now the
  `Schicht_Segment` gate at `EngineeringRulesTests.cs` L65 with `LayerSegmentBaseline = []`.

## Consequences

Positive: a single, evidence-based, peer-calibrated backlog that avoids pattern-theater; the
substantial adopts (TurnServer split, replay-window dedup, `with`-based param cloning, drift + gate
fix) are the ones that reduce real risk (off-by-one SRTP/SRTCP sync, hand-copy bug vector,
guardrail blind spot). The `TurnServer` decomposition and HARD-G1 fix have since landed and are
code-verified.

Tradeoffs: the assessment is a judgement layer, not a gate — its "reject" decisions rely on peer
calibration that a future contributor could disagree with; they are recorded here so the rejection
is a decision, not an oversight. The report itself is gitignored/local ("Read-only Befund. Keine
Edits."), so this ADR is its durable record.

**Log↔code divergence:** the assessment lists R-items with line numbers as of 2026-07-17; several
have since been implemented (R3 `TurnServer` decomposed per graphify; HARD-G1 drift closed →
`Core.Domain.Security`, gate at L65). Line numbers in the assessment are historical and were not
re-verified; the direction (harden-not-rebuild, merged backlog, the reject list) is current.

## Guardrails

- Refactoring adopts a catalog item only if it also reads as debt against the VoIP peer set — no
  blind catalog application, "nicht 23/23".
- The R-list and Paket G stay one merged, prioritised order.
- Wire/crypto/FSM hot-path types keep loose primitives and RFC-atomic methods (no wrapping, no
  method-split) — the explicit reject list stands unless a peer-calibrated reason changes.
- Structural refactors preserve the empty layering/namespace baselines and the ≤1000-line rule.

## Sources
- Logs: docs/archive/agent-log/2026-07-17-refactoring-assessment.md
- Code: tests/CalloraVoipSdk.ArchitectureTests/EngineeringRulesTests.cs
  (LayeringBaseline L20, LayerSegmentBaseline L63, namespace gate L65),
  src/Core/Infrastructure/Turn/Server/TurnServer.cs (L26, extracted collaborators via graphify),
  src/Core/Domain/Calls/Call.cs (rich aggregate), ENGINEERING_RULES.md (R1–R6, K1–K5)
- Marker: R1–R10, HARD-G1 (=R1), HARD-G2 (⊇R3), HARD-G3 (=R2), HARD-R5, Paket G
