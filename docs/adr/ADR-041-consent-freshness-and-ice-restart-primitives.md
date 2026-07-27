# ADR-041: ICE Consent-Freshness and Restart as Built-but-Unwired SIP Primitives

Status: Accepted
Date: 2026-07-09

## Context

Two ICE liveness concerns follow pair selection: **consent freshness** (RFC 7675 — periodic STUN
checks that keep permission to send to a peer, so a moved/gone peer stops receiving media) and
**ICE restart** (RFC 8445 §9.1.1.1 — a re-negotiation that discards the check state and re-gathers
when the peer's ufrag/pwd change). Both were built for the SIP path as pure, deterministic units
in the ICE epic (I6, I8).

The honest situation this ADR records is that **both are built and tested but neither is wired
into the SIP call lifecycle**. The I6 log flagged that no caller starts the consent monitor after
nomination and that the reaction to consent loss (terminate vs. restart) was an open founder
decision. The I8 log then found the wiring was materially larger than first estimated — the STUN
probe lives inside `CallIceAgent`, not the orchestrator, so belt-and-braces wiring would require
making `CallIceAgent` stateful and `IAsyncDisposable`, having it hold the nominated pair and own an
`IceConsentMonitor`, plus a new `IIceRestartCoordinator` application port. That wiring was scoped
as a fresh-context ~200–300-line change touching the media hot path — and, per current code, it
was never done on the SIP path.

### Verified current state (graphify + logs)

- **Consent policy.** `IceConsentFreshnessPolicy`
  (`src/Core/Application/Media/Ice/IceConsentFreshnessPolicy.cs`): `ConsentExpiry` = 30 s
  (RFC 7675 §5.1); `NextCheckDelay(random01)` = base interval × 0.8–1.2 (ICE Ta pacing, RFC 8445
  §14.3); `IsConsentFresh(lastConfirmed, now)`. The constructor rejects an interval that is not
  positive and shorter than the 30 s expiry.
- **Consent monitor.** `IceConsentMonitor` (`.../Ice/IceConsentMonitor.cs`): an `IAsyncDisposable`
  background loop that periodically fires an injected consent-check delegate, refreshes the
  confirmation time on an answer, and calls `onConsentLost` once when no answer arrives within the
  expiry. Clock/delay/randomness are injectable (deterministic tests); `Start`/`DisposeAsync` are
  idempotent under a lock + `_disposed` flag.
- **Consent wiring on the SIP path: absent.** `IceConsentMonitor` is referenced only by the
  Infrastructure bundle-level ICE subsystem (`IceMediaConsentSession`, `IceNominationDriver`) and
  the TURN keepalive loop — **not** by `CallIceAgent`, `CallMediaOrchestrator`, or any SIP-path
  type. `CallIceAgent` is a stateless `internal sealed class` (no `IAsyncDisposable`), returns a
  one-shot `CallIceSelectionResult`, and holds no nominated pair after selection.
- **Restart detector.** `IceRestartDetector.IsRestart(current, incoming)`
  (`.../Ice/IceRestartDetector.cs`): pure comparison — a re-negotiation is a restart when the
  peer's ice-ufrag and/or ice-pwd changed (RFC 8445 §9.1.1.1); first negotiation and ICE removal
  are not restarts. Re-gathering already yields fresh local ufrag/pwd.
- **Restart wiring: absent everywhere.** `IceRestartDetector` is referenced by **no** production
  type (only its own file); the planned `IIceRestartCoordinator` application port does not exist
  (`RequestRestartAsync` appears nowhere in `src/`). Restart detection is an unconsumed primitive.

## Decision

Build consent freshness and restart detection as **pure, injectable, separately-tested primitives**
and **defer their SIP-path wiring** rather than bolt them onto the media hot path under time
pressure:

1. **`IceConsentFreshnessPolicy`** owns the RFC 7675 timing (30 s expiry, Ta-paced interval) as
   pure functions; **`IceConsentMonitor`** owns the loop with injected clock/delay/check and a
   single `onConsentLost` callback, idempotent to start/dispose.
2. **`IceRestartDetector.IsRestart`** is a pure ufrag/pwd comparison (§9.1.1.1), no lifecycle.
3. **SIP-path wiring is explicitly deferred.** Belt-and-braces wiring requires making
   `CallIceAgent` stateful + `IAsyncDisposable`, having it own the monitor and hold the nominated
   pair, and introducing an `IIceRestartCoordinator` port whose consent-loss reaction is
   founder-decided (restart primary, call-termination fallback). Because that touches the media hot
   path, it is a fresh-context package, not a tail-end of I6/I8.

Crux: separate the *timing/detection logic* (pure, provable now) from the *lifecycle wiring*
(stateful, hot-path, founder-gated) and refuse to conflate a built primitive with a working
feature.

## Consequences

Positive: the RFC 7675 timing and the §9.1.1.1 restart trigger are captured as deterministic,
injectable units with known-answer tests (freshness at 29/30/31 s, interval scaling/clamp, monitor
loses consent after expiry / stays fresh on success / stops cleanly on dispose / dispose is
idempotent; restart-detector cases + a re-gather-produces-fresh-credentials test on
`CallIceAgent`). When wiring lands, it composes a proven loop rather than new timing logic.

Honest limits / divergence — this is the load-bearing part of the ADR:

- **Neither primitive runs on the SIP path.** No SIP-path caller starts `IceConsentMonitor` after
  nomination, so a nominated SIP pair is **not** kept alive or torn down on consent loss by this
  code; `IceRestartDetector` is invoked by nothing in production. The RFC 7675 / §9.1.1.1
  *behaviour* is therefore **not** claimed for SIP calls — only the building blocks exist.
- **Consent freshness is live only on the bundle/WebRTC path**, where `IceMediaConsentSession` /
  `IceNominationDriver` consume the monitor. That subsystem is out of this cluster's scope
  (ADR-010 orbit); this ADR does not assert the SIP agent shares it.
- **The consent-loss reaction is undecided for SIP** (terminate the call vs. request an ICE
  restart). The founder direction noted in I8 (restart primary, terminate fallback) was captured
  as intent, not implemented.
- **Base interval (5 s) is an engineering choice**, not an RFC default — sized so several checks
  fit in each 30 s window.

## Guardrails

- `ConsentExpiry` MUST be 30 s (RFC 7675 §5.1); the check interval MUST be positive and shorter
  than the expiry (constructor-enforced); randomization is Ta pacing (RFC 8445 §14.3), not RFC 7675.
- `IceConsentMonitor.Start` / `DisposeAsync` MUST be idempotent; `onConsentLost` fires at most once.
- `IceRestartDetector.IsRestart` MUST treat only a ufrag/pwd change as a restart — first
  negotiation and ICE removal are not restarts.
- Documentation MUST NOT claim SIP-path consent freshness or ICE restart until a caller starts the
  monitor after nomination and a restart coordinator (or explicit terminate) reacts to consent
  loss; today both are unwired primitives on the SIP path.

## Sources

- Logs: docs/archive/agent-log/2026-07-09-dev-ice-i6-consent-freshness.md;
  docs/archive/agent-log/2026-07-09-dev-ice-i8-restart.md
- Code: `IceConsentFreshnessPolicy.cs`; `IceConsentMonitor.cs`; `IceRestartDetector.cs`
  (all src/Core/Application/Media/Ice/); `CallIceAgent` (stateless, no monitor reference);
  consumers of `IceConsentMonitor` = `IceMediaConsentSession`, `IceNominationDriver`
  (src/Core/Infrastructure/Stun/Ice/) + `TurnAllocationRefreshLoop` only; no
  `IIceRestartCoordinator` / `RequestRestartAsync` in `src/`
- Tests: IceConsentFreshnessTests (12); IceRestartDetectorTests (6);
  CallIceAgentTests.Restart_gathering_produces_fresh_ice_credentials
- Marker: RFC 7675 §5.1; RFC 8445 §9.1.1.1, §14.3; I6/I8
