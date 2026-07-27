# ADR-002: UAS Redirect (RFC 3261 §8.3) — RedirectAsync on ISipCallSession

Status: Accepted
Date: 2026-04-09
RFC reference: RFC 3261 §8.3

---

## Context

RFC 3261 §8.3 defines redirect server behavior: when a UAS receives an INVITE
it does not wish to answer itself, it MAY respond with a 3xx response whose
Contact header lists alternative target URIs. The UAC then retries at those
locations without any involvement from the original UAS.

Common use cases:
- **Call deflection to voicemail** — phone is busy, redirect to voicemail URI
- **Multi-device forwarding** — reject on one device, redirect to another
- **Unconditional call forwarding** — always redirect to a different number

The SDK previously only supported rejecting inbound calls via `HangupAsync`
(which sends `486 Busy Here`). There was no way to send a `3xx` response.

---

## Decision

Add `RedirectAsync(IReadOnlyList<string> contactUris, int statusCode = 302, CancellationToken ct)` to `ISipCallSession`.

**Semantics:**
- Valid only for inbound dialogs (`IsInbound == true`) in `Ringing` state.
- Sends a 3xx response from the inbound INVITE server transaction.
- `Contact` header lists the caller-supplied URIs (not the local contact).
- `Record-Route` is explicitly **removed** from the response (§8.3: redirect server does not forward routing state; the UAC creates a new dialog to the Contact target).
- `To` header carries the local tag (§8.2.6.2).
- Dialog transitions to `Terminated` immediately after the redirect response is sent.
- Default status code is `302 Moved Temporarily`; `300`, `301`, `305`, and `380` are also supported.

**Validation:**
- Empty contact list → `ArgumentException`
- Status code outside 300–399 → `ArgumentOutOfRangeException`
- Wrong state → `InvalidOperationException`

---

## Consequences

**Positive:**
- Enables call deflection / forwarding without rejecting with a busy response.
- Zero breaking change — `ISipCallSession` gains a new method with a default parameter.
- `HangupAsync` behavior is unchanged (still sends 486).

**Negative:**
- Callers must ensure redirect URIs are valid SIP URIs — no validation is performed.
- The 3xx approach only works if the UAC supports redirect handling per §8.1.3.4.
  Peers that ignore 3xx will see the call fail rather than forward.

---

## Alternatives considered

1. **Overload HangupAsync with a redirect option** — Rejected. The method contract
   ("terminate the dialog") would be misleading for a redirect, which is semantically
   different from rejection.
2. **Emit a domain event and handle in application layer** — Rejected. The application
   layer would need access to internal SIP transaction state to send the 3xx response,
   violating the infrastructure abstraction boundary.
3. **Always send 302 (no status-code parameter)** — Rejected. 301 Moved Permanently
   and 300 Multiple Choices are valid RFC 3261 response codes for distinct semantics
   (permanent vs. temporary relocation, multiple alternatives).
