# ADR-027: SRTP Continuity Across Re-INVITE — Hold/Unhold Key Stability and Peer Rekey

Status: Accepted
Date: 2026-07-09

## Context

An established SRTP call does not stay static: hold/unhold and other mid-dialog re-INVITEs
re-run offer/answer. Two failure modes had to be closed. First, a hold/unhold re-offer
originally dropped the profile back to plain `RTP/AVP` with no `a=crypto` — a **security
downgrade** of a running encrypted call (medium finding from the offer-SDES review, log
`2026-07-09-dev-srtp-offer-sdes`). Second, a re-INVITE that genuinely changes the media (new
peer SDES key, new endpoint, new codec) must actually **rekey** — rebuild the media session
with the new keys — rather than keep decrypting with stale keys. Both must be achieved without
touching the initial-call fire path and without an in-place rekey mutation of the live crypto
context on the media hot path (thread-safety-critical).

### Verified current state (graphify + code)

- **Hold/unhold reuses the live key rather than rekeying.** `SdesCryptoSelector.BuildOffer(keyParams)`
  (`src/…/Sdp/OfferAnswer/SdesCryptoSelector.cs`, graphify community 275) builds an offer that
  reuses an already-issued inline key; its XML doc states: *"Used on a re-offer (hold/unhold)
  of an SRTP call so the offered key stays identical to the running media context — the peer
  keeps decrypting without a rekey."* `BuildDefaultOffer()` delegates to `BuildOffer(fresh)`,
  so the two are semantically identical apart from key provenance.
- **The running key is snapshotted once and threaded through re-offers.** `SipCoreCallChannel`
  holds `_activeLocalSrtpKeyParams` (volatile), set in `TryPublishMediaParameters` from the
  enriched outbound-encrypt key; a `BuildReofferSdpOptions()` helper reads it once (consistent
  snapshot) and hold/unhold re-offers advertise `RTP/SAVP` + the same key. A plain call
  re-offers plain (log `2026-07-09-dev-srtp-holdunhold-continuity`).
- **Genuine media change triggers a rebuild, not a mutation.** `TryRepublishMediaParametersOnRekey`
  runs only on the *second* Established (`_mediaParametersFired == 1`) and only when the media
  signature genuinely changed (`RekeySignature` = `RemoteEndPoint|PayloadType|CodecName|
  SrtpSuite|Local|Remote-Keys`), so a retransmitted/no-op re-INVITE does not re-fire. It
  re-publishes `MediaParametersNegotiated`; the `CallMediaOrchestrator` builds a fresh media
  session with the new keys (log `2026-07-09-dev-peer-rekey-slice1`). The SRTP enrichment used
  by both the initial and rekey paths was extracted to `CallMediaParametersSrtpEnricher`
  (graphify community 522) so both paths share identical key-recovery logic.
- **Both directions rekey.** An outbound (unhold) rekey works because the transaction service
  calls `SetRemoteSdp(2xx body)` before `TransitionTo(Established)` (slice 1). An inbound
  re-INVITE/UPDATE was fixed to also record its own answer:
  `ISipCallSession.LocalSdp` (default interface member, no test churn), `SipCallSession`
  `_localSdp`/`SetLocalSdp` under `_sync`, and `SipCallSessionInboundService` calls
  `SetRemoteSdp` + `SetLocalSdp` **before** `TransitionTo`. The channel then enriches with
  `session.LocalSdp ?? _localAnswerSdp ?? _localOfferSdp`, so an inbound re-INVITE rekeys both
  the new local and new peer key (log `2026-07-09-dev-peer-rekey-slice2`).
- **Fail-closed is preserved across rekey.** The `sdesUsable` gate (both keys + suite match;
  ADR C04-02) applies on the rekey path too — never half-keyed. A policy violation detected on
  rekey calls `TerminateForSrtpPolicyViolationAsync`. Peer hold (`a=sendonly`) transitions to
  `OnHold`, not a second `Established`, so the Established-gated rekey does not run.

## Decision

Keep SRTP continuous across re-INVITE via two distinct mechanisms:

1. **Key stability for hold/unhold.** A hold/unhold re-offer of a running SRTP call re-advertises
   `RTP/SAVP` with the **same** master key/salt that keys the live context. Because the key is
   identical, the running `SrtpContext` stays valid — no in-place rekey, no downgrade
   (`SdesCryptoSelector.BuildOffer` + `_activeLocalSrtpKeyParams` snapshot). A plain call stays
   plain.
2. **Rebuild-on-change for genuine rekey.** A re-INVITE that changes the media signature
   re-publishes negotiated parameters; the orchestrator builds a *new* media session with the
   new keys. Signature comparison suppresses retransmission/no-op re-INVITEs. Both outbound
   (unhold) and inbound re-INVITE/UPDATE record remote **and** local SDP before the state
   transition, so rekey covers both directions.

**Crux:** hold/unhold is solved by making the key *stable* (avoiding rekey entirely), and true
key change is solved by *rebuilding the session* off a debounced re-publish — never by mutating
a live crypto context on the media hot path.

## Consequences

Positive: no downgrade of a running SRTP call on hold/unhold; genuine peer rekey (new key,
codec, or endpoint) works in both directions; the initial-call fire path is provably untouched
(rekey is strictly additive, gated on the second Established); the media hot path is never
mutated in place. Reviews: hold/unhold APPROVED (no findings), both rekey slices APPROVED
(no findings).

Tradeoffs / honest divergence:
- **Hold/unhold is key-reuse only, not true rekey.** If a peer returns a *new* key in a
  hold/unhold answer, `_activeLocalSrtpKeyParams` keeps the initial key and the change is not
  renegotiated on that specific path. Peer-initiated rekey with a new key is handled by the
  rebuild path (slices 1/2), which is the general mechanism; the hold/unhold path is the
  narrow key-stable optimization (log `-holdunhold-continuity`).
- **Race:** hold before the first media publish → `_activeLocalSrtpKeyParams` is null → plain
  re-offer. No active SRTP context exists at that instant, so no security break (log
  `-holdunhold-continuity`).
- **Re-entrancy note carried forward:** `TryRepublishMediaParametersOnRekey` is safe today
  because `StateChanged` fires strictly serially from `SipCallSession.TransitionTo` under
  `lock`; a second concurrent fire source would need the guard re-evaluated (flagged in slice 1
  for slice 2, still standing as a design constraint, log `-peer-rekey-slice1`).
- **Test-coverage caveat:** a dedicated `SipCallSessionInboundService` handler test asserting
  `SetRemoteSdp`/`SetLocalSdp` ordering before `TransitionTo` is recommended follow-up; today
  the ordering is covered indirectly by the green SIP regression + code inspection (log
  `-peer-rekey-slice2`). This ADR does not claim that dedicated test exists.

## Guardrails

- A running SRTP call never re-offers plain on hold/unhold; it re-advertises `RTP/SAVP` with
  the stable live key (`_activeLocalSrtpKeyParams` snapshot via `BuildReofferSdpOptions`).
- Rekey fires only on the second Established and only on a genuine media-signature change;
  retransmissions/no-ops do not re-fire.
- Rekey records both local and remote SDP before the state transition (outbound and inbound
  paths symmetric); the `sdesUsable` gate applies — never half-keyed.
- The initial-call fire path is unchanged by the rekey addition.
- Live crypto contexts are never mutated in place; rekey rebuilds the media session.
- SRTP policy violation on rekey terminates the call (`TerminateForSrtpPolicyViolationAsync`).

## Sources

- Logs: `docs/archive/agent-log/2026-07-09-dev-srtp-holdunhold-continuity.md` (key stability),
  `2026-07-09-dev-peer-rekey-slice1.md` (outbound re-publish/rebuild),
  `2026-07-09-dev-peer-rekey-slice2.md` (inbound bidirectional rekey);
  origin of the downgrade finding: `2026-07-09-dev-srtp-offer-sdes.md`.
- Code (graphify-verified): `SdesCryptoSelector.cs` (community 275, `BuildOffer`/`BuildDefaultOffer`),
  `CallMediaParametersSrtpEnricher.cs` (community 522, shared enrichment),
  `SipCoreCallChannel` (`_activeLocalSrtpKeyParams`, `TryRepublishMediaParametersOnRekey`,
  `RekeySignature`), `SipCallSession`/`ISipCallSession` (`LocalSdp`/`SetLocalSdp`),
  `SipCallSessionInboundService` (SetRemote/SetLocal before TransitionTo).
- Markers / RFC: RFC 3264 §5.1/§8 (offer/answer, re-INVITE), RFC 4568 (SDES);
  ENGINEERING_RULES K1 (fail-closed / no downgrade), K3 (threading: serial StateChanged, volatile
  snapshot), K7 (RFC refs).
