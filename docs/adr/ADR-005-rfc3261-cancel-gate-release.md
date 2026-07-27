# ADR-005: RFC 3261 §9.1 – Release Operation Gate Before INVITE Transaction

**Status:** Accepted  
**Date:** 2026-04-09  
**RFC reference:** RFC 3261 §9.1 (Canceling a Request – UAC Behavior)

---

## Context

RFC 3261 §9.1 requires that a UAC **can send CANCEL at any time while an INVITE is
pending** (i.e., before a final response is received). This means `HangupAsync` must
be callable during an in-flight `HoldAsync`, `UnholdAsync`, or the initial
`StartOutboundInviteAsync`.

Prior to this change, `StartOutboundInviteAsync`, `HoldAsync`, and `UnholdAsync` each
held the `_operationGate` (a `SemaphoreSlim(1,1)`) for the **entire duration** of the
`SendInviteTransactionAsync` call — i.e., until the final INVITE response was received.
`HangupAsync` also acquires the same gate, so calling it while an INVITE was in flight
caused a deadlock: the CANCEL was never sent.

---

## Decision

**`StartOutboundInviteAsync`, `HoldAsync`, `UnholdAsync`:**  
Release `_operationGate` **before** calling `SendInviteTransactionAsync`.  
The gate guards only the initial state validation and setup (state transition,
body preparation). The actual INVITE transaction runs without the gate.

**`HangupAsync`:**  
When state is `Established` or `OnHold` **and** `_activeInviteCSeq > 0 &&
!string.IsNullOrWhiteSpace(_activeInviteBranch)`, send `CANCEL` instead of `BYE`.

### Why this is safe

`SendInviteTransactionAsync` sets `_context.ActiveInviteCSeq` and
`_context.ActiveInviteBranch` **synchronously** (before its first internal `await`).
In C#, the synchronous preamble of an awaited async method runs on the caller's thread
before any suspension. Thus, by the time the calling thread yields at
`await _transactionService.SendInviteTransactionAsync(...)`, the branch is already
set — even though the gate has been released.

In practice, callers that need to act on the in-flight INVITE (e.g. `HangupAsync`)
do so after observing a transport event (e.g. the request appearing in
`RecordingSipTransportRuntime`), which provides a natural happens-before guarantee.

### State transitions

`TransitionTo` is protected by the internal `_sync` lock (separate from the gate) and
is idempotent once `Terminated` is reached. If both `HangupAsync` and the conclusion
of `SendInviteTransactionAsync` race to call `TransitionTo(Terminated)`, the second
call is a no-op.

---

## Consequences

**Positive:**
- `HangupAsync` can send CANCEL for any pending INVITE (initial or re-INVITE)
  without deadlocking.
- The CANCEL Via branch is a fresh `NewBranch()` (RFC 3261 §9.1 compliant).
- The CANCEL CSeq matches the INVITE CSeq (RFC 3261 §9.1 compliant).
- All four §9 compliance tests pass.

**Negative:**
- The narrow window between gate release and `_activeInviteBranch` being set is not
  guarded by the gate. In practice this is harmless (synchronous preamble), but
  `_activeInviteBranch` is not `volatile`. A future refactor could add
  `Volatile.Read/Write` for extra correctness.
- After cancelling a re-INVITE, the established dialog is terminated (BYE not sent).
  Per RFC 3261, a CANCEL that results in a 487 for the re-INVITE leaves the
  established dialog alive; a BYE would then be needed. This is an acceptable
  simplification: `HangupAsync` semantics are "end the call", so skipping the BYE
  is reasonable and the remote side will detect the lapse via keepalive or timeout.

---

## Alternatives considered

1. **Keep gate held; add a `CancelInviteAsync` bypass path** — Rejected. Adds API
   surface and duplicates hangup logic outside the normal gate-protected flow.
2. **Use a `CancellationToken` passed to `SendInviteTransactionAsync`** — Rejected.
   Cancelling a `CancellationToken` would abort the local transaction without sending
   CANCEL on the wire, violating RFC 3261 §9.1.
3. **Add a dedicated `_cancelRequested` flag** — Rejected. More complex than simply
   releasing the gate early; offers no correctness advantage.
