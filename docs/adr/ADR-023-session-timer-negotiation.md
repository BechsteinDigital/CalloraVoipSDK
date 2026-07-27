# ADR-023: RFC 4028 Session-Timer Negotiation

Status: Accepted
Date: 2026-07-09

## Context

The SDK negotiates the RFC 4028 session timer (`Session-Expires` / `Min-SE` / `refresher`),
which governs dialog lifetime and decides which party refreshes. This surface was **completely
untested** — a misparse either drops a live call at the interval or refreshes needlessly. It is
a purely deterministic, side-effect-free policy surface, so it is well suited to being pinned
by known-answer tests as the source of truth for the behaviour.

### Verified current state (graphify + logs)

- `SipSessionTimerPolicy`
  (`src/Core/Infrastructure/Sip/Signaling/Formatting/SipSessionTimerPolicy.cs`, L8) exposes:
  `TryValidateInboundRequest`, `TryResolveNegotiation`, `ApplyOutboundOfferHeaders`,
  `ApplyResponseHeaders`, `ApplyTooSmallResponseHeaders`, plus parsers
  `TryParseSessionExpires` / `TryParseMinSe` / `TryParseRefresherRole` and `AppendToken`.
- `TryValidateInboundRequest`: no header → default `1800;refresher=uas`; valid interval →
  normalised to `;refresher=uas`; below `Min-SE` → **422 "Session Interval Too Small"**;
  unparseable → 400.
- `TryResolveNegotiation`: refresher role per RFC 4028 — `uac` → requester refreshes, `uas` →
  responder, no param → requester; null / "" / `0;…` → no negotiation.
- `ApplyOutboundOfferHeaders`: emits `Supported: …timer`, `Session-Expires=1800;refresher=uac`,
  `Min-SE=90`.

## Decision

Negotiate the session timer strictly per RFC 4028: default the interval when absent, normalise
the refresher on validation, reject too-small intervals with 422 + `Min-SE`, resolve the
refresher role from the `refresher` param (uac=requester, uas=responder, absent=requester), and
offer `Session-Expires`/`Min-SE` with `Supported: timer`. Lock the parsing, validation, refresher
resolution, and offer emission with deterministic known-answer tests.

Crux: the refresher role and the 422/Min-SE floor are the two places a misparse silently drops or
churns a dialog — pin exactly those with explicit vectors.

## Consequences

Positive: RFC-4028-correct session-timer parsing, validation, refresher resolution, and offer
emission, regression-locked deterministically; too-small intervals are rejected with the correct
422 + `Min-SE` rather than accepted and later dropped.

Tradeoffs / honest scope: the tests are test-only (no production change in that slice) and cover
parsing/validation/refresher/offer. The *actual periodic refresh transmission* driven off the
negotiated interval and refresher role is not asserted end-to-end here — the policy that decides
lifetime and refresher is proven; the timer-fired re-INVITE/UPDATE loop is a downstream concern.

## Guardrails

- Absent `Session-Expires` on an inbound request defaults to `1800;refresher=uas`.
- An interval below `Min-SE` MUST yield 422 with the `Min-SE` header, never silent acceptance.
- Refresher role follows the `refresher` param: uac=requester, uas=responder, absent=requester.
- Outbound offer carries `Supported: timer` + `Session-Expires` + `Min-SE`.

## Sources

- Logs: docs/archive/agent-log/2026-07-09-dev-b8-session-timer-tests.md
- Code: `SipSessionTimerPolicy`
  (src/Core/Infrastructure/Sip/Signaling/Formatting/SipSessionTimerPolicy.cs)
- Tests: SipSessionTimerPolicyTests (14 cases)
- Marker: RFC 4028 (Session Timers); B.8 slice 5
