# ADR-017: REGISTER Expires / Refresh Lifecycle

Status: Accepted
Date: 2026-07-09

## Context

Behind NAT against a real registrar (sipgate), the SDK's REGISTER refresh interval
churned — observed refreshes of 22s → 5s → 5s → 46s instead of stable minutes. A
churning REGISTER continually re-opens the NAT pinhole and destabilises the binding,
which in turn can starve inbound INVITE delivery.

Two distinct root causes were found:

1. **Wrong grant read.** `SipRegistrationService.TryGetEffectiveExpires` took the
   *first* `expires=` it saw in the raw multi-binding Contact of the 200 OK. In a
   multi-binding 200 OK that is the *remaining lifetime* of an arbitrary (often stale)
   binding that counts down per poll — so each refresh computed a shorter and shorter
   base. RFC 3261 §10.2.1.1 requires the per-Contact `expires` of **our own** binding
   to take precedence over the top-level `Expires`.
2. **Expires dropped on the wire in responses.** `SipHeaderRowRules.RequestOnlyHeaderNames`
   classified `Expires` as request-only, so every `Expires` header was discarded during
   *response* parsing. That is RFC-wrong (RFC 3261 §10.3: top-level `Expires` is legal in a
   REGISTER 200 OK; RFC 6665: SUBSCRIBE 200 OK) and left the top-level branch of the
   selection logic dead.

### Verified current state (graphify + source)

- `SipRegistrationService.TryGetEffectiveExpires` (`src/Core/Infrastructure/Sip/Signaling/Registration/SipRegistrationService.cs`,
  method at L701) now resolves in RFC order: **(1)** own-Contact binding via
  `NormalizeContactCore` URI match (L708–713), **(2)** top-level `Expires` header (L715–717,
  §10.3), **(3)** longest remaining binding (L719–725), **(4)** requested fallback (L727).
  Bindings come from a single `ParseRegisteredBindings` pass reused in the success path.
- **The wire tripwire is resolved.** `SipHeaderRowRules.RequestOnlyHeaderNames`
  (`src/Core/Infrastructure/Sip/Wire/SipHeaderRowRules.cs` L41–56) **no longer contains
  `Expires`**; L45–46 carries an explicit comment that `Expires` is valid in responses
  (RFC 3261 §10.3 / RFC 6665) and must survive response parsing. The follow-up package the
  analysis log flagged has since been implemented — see Consequences.
- `Min-Expires` remains response-only (`ResponseOnlyHeaderNames`, L62). A 423 triggers a
  retry with the server's `Min-SE`: `ExecuteRegisterAsync` reads `TryGetMinExpires`
  (L301–309, L749) and re-registers with the raised interval.
- Refresh scheduling lives in the line channel (`SipLineChannel.ComputeRefreshDelay`,
  per log): refresh at ~80% of the granted lifetime, floored by a `MinRefreshSeconds = 15`
  secondary guard, itself capped at `baseline-1` so short grants never refresh after the
  binding dies.

## Decision

Drive the refresh clock off the **effective grant for our own binding**, selected in strict
RFC 3261 §10.2.1.1 / §10.3 precedence, and keep `Expires` alive through response parsing so
the top-level branch is real. Honour a 423 by re-registering with `Min-Expires`. Schedule the
refresh at 80% of that grant with a hard floor and a hard ceiling.

Crux: the bug was never in the refresh timer — it was in *which number* fed it. Fix the
selection (own binding first, not first-seen), unblock the wire (Expires survives), and the
timer stabilises without a churn heuristic.

## Consequences

Positive: stable multi-minute refresh cadence against multi-binding registrars; correct
behaviour for registrars that only send a top-level `Expires`; 423/Min-SE handled; the refresh
floor prevents churn while the ceiling prevents refreshing a binding that already expired.

Divergence, honestly stated: the 2026-07-08/09 logs describe the top-level `Expires` branch as
*defensive but dead* because of the `RequestOnlyHeaderNames` tripwire, with a tripwire test
(`OwnBinding_selected_evenWhenTopLevelExpiresPresent`) guarding the workaround. **The current
code has moved past that state** — `Expires` is no longer request-only and the top-level branch
is live. The ADR records the resolved end-state, not the interim workaround.

Not proven by tests: end-to-end registrar stability (whether steadier refresh measurably
improves inbound INVITE delivery) — that stays a real-world observation; delivery precedes the
INVITE and was never causally provable from the log alone.

## Guardrails

- Own-Contact per-Contact `expires` MUST outrank top-level `Expires` (§10.2.1.1); never read
  the first `expires=` of a multi-binding Contact as the grant.
- `Expires` MUST survive response parsing; do not re-add it to `RequestOnlyHeaderNames`.
- Refresh delay stays within `[MinRefreshSeconds, baseline-1]`; short grants must refresh
  before the binding dies.
- A 423 MUST re-register with the offered `Min-Expires`.

## Sources

- Logs: docs/archive/agent-log/2026-07-08-dev-sip-registration-expires.md;
  docs/archive/agent-log/2026-07-09-analysis-b7-expires-in-responses.md
- Code: `SipRegistrationService.TryGetEffectiveExpires` / `TryGetMinExpires`
  (src/Core/Infrastructure/Sip/Signaling/Registration/SipRegistrationService.cs);
  `SipHeaderRowRules` (src/Core/Infrastructure/Sip/Wire/SipHeaderRowRules.cs);
  `SipLineChannel.ComputeRefreshDelay`
- Tests: SipRegistrationExpiresTests
- Marker: RFC 3261 §10.2.1.1, §10.3; RFC 6665; B.7 (Expires-in-responses follow-up)
