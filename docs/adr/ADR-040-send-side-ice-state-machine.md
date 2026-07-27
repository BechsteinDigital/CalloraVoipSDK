# ADR-040: Send-Side ICE State Machine on the SIP Path (RFC 8445)

Status: Accepted
Date: 2026-07-09

## Context

The SIP media path needed real ICE (RFC 8445), not the placeholder it had. The pre-existing
`CallIceAgent` ran a sequential, first-wins probe over candidate pairs with **no** check list,
no candidate-pair priority, no role, no wire attributes, and no nomination — enough to pick a
reachable pair on a LAN, but not RFC-conformant ICE. The founder directive was "volles ICE,
komplett umsetzen".

The work was sliced deliberately so the live media path never regressed: build the pure,
deterministic pieces first (check-list construction, pair priority, the check FSM) with no wiring,
then thread them into `CallIceAgent`, then put the check attributes on the wire, then add regular
nomination. Each slice explicitly refused to claim "ICE works" until it was wired and evidenced.

This ADR covers the **outbound / controlling-and-controlled send model** on the SIP path: the
agent forms a check list, drives it as a state machine, sends STUN Binding checks carrying the ICE
attributes, and nominates the selected pair. Inbound check processing (§7.3), consent freshness,
and ICE restart are separate concerns (C11-02); the bundle-level WebRTC ICE subsystem is separate
again (ADR-010 orbit).

### Verified current state (graphify + logs)

- **Check list.** `IceCheckList.Create(locals, remotes, role)`
  (`src/Core/Application/Media/Ice/IceCheckList.cs`) pairs candidates by component/transport/
  address-family (RFC 8445 §6.1.2.2), prunes a server-reflexive local that is redundant with its
  base (§6.1.2.4), orders by pair priority, caps at 100 pairs (§6.1.2.5, `MaxCandidatePairs`),
  and assigns initial states by foundation freezing (§6.1.2.6: one `Waiting` per foundation, the
  rest `Frozen`). Pair priority is `IceCandidatePair.ComputePriority(role, localPrio, remotePrio)`
  = RFC 8445 §6.1.2.3 (`2^32·min(G,D) + 2·max(G,D) + (G>D?1:0)`).
- **FSM scheduler.** `IceConnectivityScheduler.RunAsync(checkList, checkDelegate, ct)`
  (`.../Ice/IceConnectivityScheduler.cs`) runs the list as a state machine (§6.1.4.2 ordinary
  scheduling, §7.2.5.3.3 state advancement): each iteration takes the highest-priority `Waiting`
  pair → `InProgress` → the check delegate → `Succeeded`/`Failed`; a completed check unfreezes
  same-foundation `Frozen` pairs; it returns the first pair that passes (highest-priority valid).
- **Role.** `CallIceAgent.SelectCandidatePairAsync` (`src/Core/Application/Media/CallIceAgent.cs`
  L247) derives the role from `parameters.IceControlling` — the SDP offerer is controlling, the
  answerer controlled (RFC 8445 §5.1.1); the infrastructure adapter carries the direction in. The
  role feeds both the pair-priority formula and the role attribute on each check. (This supersedes
  the "fixed Controlling" simplification the I3/I4 logs described as still-open.)
- **Wire attributes.** `StunIceCheckAttributes.Build(priority, isControlling, tieBreaker,
  useCandidate)` produces PRIORITY + ICE-CONTROLLING/ICE-CONTROLLED (+ USE-CANDIDATE when
  nominating). `StunIceProbe.TryCheckConnectivityAsync` passes them through
  `IStunClient.QueryBindingAsync`'s `additionalAttributes`, placed before USERNAME/
  MESSAGE-INTEGRITY so MI covers them (ICE always has credentials). A one-off `_tieBreaker =
  IceTieBreaker.Generate()` (§5.2) is generated per agent.
- **Regular nomination.** After the scheduler returns a valid pair, a controlling agent re-checks
  the *same* pair with `useCandidate: true` and sets `IceCandidatePair.Nominated` (RFC 8445
  §8.1.1). If the nomination check is not confirmed, the pair stays selected but `Nominated =
  false` (`CallIceAgent.cs` L294–314).

## Decision

Implement send-side ICE on the SIP path as a **deterministic check-list state machine driven by
`CallIceAgent`**, built in unwired pure slices first and wired last:

1. **`IceCheckList` + `IceCandidatePair`** own pairing, srflx pruning, §6.1.2.3 pair priority,
   the §6.1.2.5 cap, and §6.1.2.6 foundation freezing — pure, known-answer-testable, no live-path
   effect until consumed.
2. **`IceConnectivityScheduler`** drives that list as the §6.1.4.2 / §7.2.5.3.3 FSM via an injected
   check delegate, returning the highest-priority valid pair.
3. **`CallIceAgent` derives the role from the offer/answer direction** (§5.1.1) and feeds it into
   both priority and the check attributes.
4. **The check attributes go on the wire** through the STUN client's `additionalAttributes`, under
   MESSAGE-INTEGRITY.
5. **Regular nomination** (§8.1.1) is a controlling-only USE-CANDIDATE re-check of the selected
   pair; a failed nomination does not discard an otherwise-valid pair.

Crux: keep the RFC's own primitives (check list, foundation freezing, pair priority, role,
USE-CANDIDATE) as separately-testable units, and never let a slice claim conformance before it is
both wired into `CallIceAgent` and evidenced on the wire against the built `StunServer`.

## Consequences

Positive: the SIP path selects a candidate pair by RFC-8445 pair priority through a real check
FSM, advertises PRIORITY + role + tie-breaker under MESSAGE-INTEGRITY, and nominates with
USE-CANDIDATE — up from a first-wins probe. Every piece is known-answer or loopback-tested against
the built `StunServer`, so the wire attributes are proven to survive encode + MI + server parse.

Honest limits / divergence:

- **Send-only model.** This agent *sends* checks and reads responses; it does **not** process
  inbound Binding requests on the media port. Role-conflict resolution (§7.3.1.1) and a peer's
  inbound USE-CANDIDATE (a controlled agent nominating from the peer's request, §7.3.1.5) are
  handled by separate primitives (`IceInboundCheckEvaluator` / `IceRoleConflict`) that today are
  consumed only by the Infrastructure bundle-level path (`IceInboundCheckProcessor` /
  `IceInboundBindingResponder`), **not** by `CallIceAgent`. Full bidirectional ICE on the SIP path
  is therefore not claimed.
- **No peer-reflexive candidates.** PRIORITY carries the local candidate priority, not the
  prflx-derived priority (§7.2.2); `IceConnectivityScheduler`'s own doc marks prflx discovery as a
  later package. Unbuilt on the SIP path.
- **Foundation freezing is latent for RTP-only calls.** With one component and distinct
  foundations, every pair starts `Waiting`; the unfreeze path is implemented and unit-tested but
  only becomes active with a second component (RTCP), which is not built here.
- **No trickle ICE.** Selection runs over the candidates present at negotiation time.

## Guardrails

- Pair priority MUST use RFC 8445 §6.1.2.3 with the role-assigned G/D; the controlling role must
  flip G/D relative to controlled (known-answer test pins the literal).
- The role MUST derive from the offer/answer direction (§5.1.1), never be hard-coded.
- Check attributes MUST be placed before MESSAGE-INTEGRITY so MI covers them; the 300 Try-Alternate
  requery MUST NOT forward them.
- USE-CANDIDATE MUST be sent only by a controlling agent, and only as a re-check of an
  already-valid pair; a failed nomination MUST keep the valid pair usable (unnominated).
- No slice may claim ICE conformance before it is wired into `CallIceAgent` and evidenced on the
  wire.

## Sources

- Logs: docs/archive/agent-log/2026-07-09-dev-ice-i1-checklist-foundation.md;
  docs/archive/agent-log/2026-07-09-dev-ice-i2b-wire-attributes.md;
  docs/archive/agent-log/2026-07-09-dev-ice-i3-fsm-connectivity.md;
  docs/archive/agent-log/2026-07-09-dev-ice-i4-nomination.md
- Code: `IceCheckList` / `IceCandidatePair` / `IceCandidatePairState`
  (src/Core/Application/Media/Ice/); `IceConnectivityScheduler.cs`; `IceRole.cs`;
  `IceTieBreaker.cs`; `CallIceAgent.SelectCandidatePairAsync` / `CheckPairAsync`
  (src/Core/Application/Media/CallIceAgent.cs); `StunIceCheckAttributes.Build`,
  `StunIceProbe.TryCheckConnectivityAsync`, `IStunClient.QueryBindingAsync`
  (`additionalAttributes`)
- Tests: IceCheckListTests; IceConnectivitySchedulerTests; StunIceCheckWiringTests;
  CallIceAgentTests (Connectivity_check_carries_local_priority_and_controlling_role,
  Controlling_agent_nominates_selected_pair_with_use_candidate,
  Pair_stays_selected_but_unnominated_when_nomination_check_fails,
  Address_family_mismatch_yields_no_candidate_pairs)
- Marker: RFC 8445 §5.1.1, §5.2, §6.1.2.2–§6.1.2.6, §6.1.4.2, §7.2.5.3.3, §8.1.1; I1/I2b/I3/I4
