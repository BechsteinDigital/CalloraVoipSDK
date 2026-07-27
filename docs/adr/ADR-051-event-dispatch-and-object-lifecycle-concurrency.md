# ADR-051: Event-Dispatch and Object-Lifecycle Concurrency Contract

Status: Accepted
Date: 2026-07-08

## Context

The SDK raises domain and facade events (call state, DTMF, transfer, quality, ICE/congestion)
from whichever thread the underlying work runs on: the SIP signalling thread for dialog events
and the media/RTCP threads for quality events. There is no dispatcher thread and no captured
`SynchronizationContext` — a handler runs inline on the thread that produced the event. That
choice keeps latency and allocation low but makes two classes of concurrency bug easy to write:

1. **Torn observation around a lock.** If the mutable state is updated under a lock and the event
   is then raised *after* the lock is released, a subscriber can read a different value than the
   one the event reports, because a second writer can move the field in the window between unlock
   and invoke. The same window lets a handler that unsubscribes mid-dispatch race the iteration,
   and lets an event fire after the owning object has been disposed.
2. **Field-by-field reads of a logically-paired state.** Two related fields (host+port of the
   advertised contact; CSeq+branch of the active INVITE) are only meaningful as a pair. Reading
   them through two separate property accesses spans two lock scopes and can straddle a write,
   yielding a host from one negotiation and a port from the next.

A separate but related lifetime hazard is the ICE path: the media-negotiation event handler is
synchronous on the signalling thread, but ICE connectivity checks are inherently async, so the
original code blocked the signalling thread with `.GetAwaiter().GetResult()` (the B.4 audit
core finding). Blocking an SDK dispatch thread on network I/O is the lifecycle inverse of the
torn-read problem: it is a correctness hazard created by mixing sync dispatch with async work.

The governing rule is ENGINEERING_RULES **K3** (Threading contracts). This ADR records the
concurrency contract for *event dispatch and object lifecycle*; the sibling C14-02 records the
lock-free *media-hot-path* contract. Allocation hygiene (C07-01) and the tap *API shape*
(C18-01) are separate decisions that obey — but do not define — this contract.

### Verified current state (graphify + code)

- **Snapshot-inside-lock, invoke-outside-lock is the house pattern.**
  `Call.TransitionTo` (`src/Core/Domain/Calls/Call.cs`, ~L370) validates and mutates state under
  `lock (_sync)` and snapshots the delegate *inside* the lock (`stateChangedSnapshot = StateChanged;
  // snapshot before releasing lock`), then invokes it after the lock is released. The other Call
  events (quality, ICE, congestion, key-frame-request) follow the same shape.
- **State is read lock-free via `volatile`.** `private volatile int _stateInt` with
  `public CallState State => (CallState)_stateInt;` — reads take no lock; every write is under
  `_sync`. This is the deliberate trade recorded in the field comment.
- **Disposed-guard before dispatch.** `SipCoreCallChannel` (`.../Sip/Adapters/SipCoreCallChannel.cs`)
  gates every inbound event handler on `Volatile.Read(ref _disposed) != 0` before touching the
  notifier, because the `-=` unsubscribe in `Dispose()` races in-flight deliveries on a background
  thread.
- **Handler iteration over a snapshot.** `SipTransportRuntime.DispatchRequest`/`DispatchResponse`
  iterate `_requestHandlers.Values.ToArray()` — a snapshot taken before iterating — so a handler
  that unsubscribes itself cannot mutate the collection under the loop.
- **Paired state read/written atomically (HARD-C1/C2).**
  `SipCallSessionContextAdapter` (`.../Sip/Signaling/Dialogs/`) exposes
  `AdvertisedPublicContact => { lock(_sync) return (host, port); }` (HARD-C1) and
  `ActiveInvite` / `SetActiveInvite` / `ClearActiveInvite` (HARD-C2) as single-lock snapshot
  APIs; the individual-field getters exist but the paired API is the documented correct path.
  `SipActiveInviteStateConsistencyTests.Active_invite_snapshot_is_observed_as_a_consistent_pair_under_concurrency`
  drives concurrent writers/readers and asserts zero torn reads.
- **Idempotent dispose via `Interlocked.Exchange`.** ~20 threaded types in `src/` open Dispose with
  `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;` (canonical:
  `MediaConnection.Dispose`, `.../Application/Media/MediaConnection.cs`).
- **Async hygiene.** `ConfigureAwait(false)` is pervasive (~674 sites in `src/`); every
  `TaskCompletionSource` in `src/` is constructed with `TaskCreationOptions.RunContinuationsAsynchronously`;
  fire-and-forget starts go through fault-observing helpers (e.g. `PhoneLine.ObserveHangupAsync`
  logs the fault; `Call` dispose-hangup uses `ContinueWith(..., OnlyOnFaulted, TaskScheduler.Default)`).
- **ICE sync-over-async removed.** `CallMediaOrchestrator` runs media setup after ICE pair
  selection on a background `Task.Run` wrapped in try/catch-and-log; `CallMediaOrchestrator` is
  no longer in the ArchitectureTests `SyncOverAsyncBaseline`, so the gate structurally forbids the
  `.GetAwaiter().GetResult()` regressing. The non-ICE path is unchanged and synchronous.

## Decision

Events fire **synchronously on the producing SDK thread** (SIP signalling or media/RTCP); the SDK
does not marshal handlers to another thread. Handlers are contractually forbidden to block or
throw. To make that sync-dispatch model correct under concurrency, every event-raising and
lifecycle path obeys the following invariants:

1. **Snapshot the delegate inside the lock, invoke outside it.** The state write and the delegate
   capture happen atomically under the object's `_sync`; the invocation runs after unlock so a
   handler cannot re-enter the lock and cannot deadlock the producer. Subscribers therefore always
   observe the state the event reports.
2. **Read hot, single-valued state lock-free via `volatile`; guard multi-field writes with the
   lock.** A scalar like `CallState` is `volatile`; a paired state is only ever exposed through an
   atomic snapshot API.
3. **Read/write logically-paired state as one snapshot (HARD-C1/C2), never field-by-field.**
   `(host, port)` and `(cseq, branch)` are published, read, and cleared as tuples under one lock
   acquisition.
4. **Guard dispatch and iteration against lifetime races.** Check the disposed flag before
   dispatching an inbound event; iterate a `.ToArray()` snapshot when a handler may unsubscribe
   itself during the loop.
5. **Dispose is idempotent and race-safe** via a single `Interlocked.Exchange(ref _disposed, 1)`
   gate at the top of Dispose.
6. **Async hygiene is non-negotiable in library code:** `ConfigureAwait(false)` on every await,
   `RunContinuationsAsynchronously` on every `TaskCompletionSource`, and fire-and-forget only
   through a helper that observes and logs the fault. No SDK dispatch thread ever blocks on async
   work (no sync-over-async), enforced for the ICE path by the ArchitectureTests baseline gate.

### Crux

The load-bearing insight is that **synchronous inline dispatch is cheap and correct only if the
producer never holds a lock across foreign handler code and never lets a second writer open a
window between the state mutation and the notification.** Capturing the delegate inside the same
lock that commits the state collapses that window to zero without keeping the lock held over the
untrusted handler. Everything else in the contract (atomic pairs, disposed-guard, snapshot
iteration, idempotent dispose, no sync-over-async) removes the remaining lifetime and ordering
races that the inline model would otherwise expose.

## Consequences

Positive: subscribers see consistent, non-torn state; producers cannot deadlock on or be blocked
by a misbehaving handler; disposal is safe against in-flight events; the ICE path no longer stalls
the signalling thread, and a gate prevents the regression.

Tradeoffs and honest divergences:

- **The contract is partly convention, partly gated.** Only two facets are mechanically enforced:
  R6 (`no sync-over-async`, baseline-gated) and R5 (no silent catch). Snapshot-inside-lock, the
  HARD-C1/C2 atomic-pair discipline, `ConfigureAwait(false)`, and `RunContinuationsAsynchronously`
  are review-enforced conventions, not gates — a new torn-read or a missing `ConfigureAwait` would
  not fail CI today. The paired-state guarantee does have a dedicated concurrency stress test, but
  it covers `ActiveInvite`, not every paired field.
- **The individual-field getters for HARD-C1/C2 state still exist** alongside the snapshot API; the
  discipline that callers use the snapshot path is documented, not compiler-forced.
- **"Handlers must not block or throw" is a contract on the consumer**, unenforceable by the SDK; a
  blocking handler will stall the producing thread by design. Frame fan-out isolates handler faults
  (see C14-02), but the top-level event delegates rely on the documented rule.
- **Two event-raising styles coexist**: capture-inside-lock (Call) and disposed-guard-then-notify
  (SipCoreCallChannel). Both are correct for their thread model; the split is deliberate, not drift.

## Guardrails

- New event-raising code snapshots the delegate inside the lock and invokes outside; reviewers
  reject "mutate under lock, raise after unlock" without an in-lock capture.
- New logically-paired mutable state ships with an atomic snapshot API and carries a HARD-C1/C2-style
  marker; individual-field access to such state is a review finding.
- Every threaded type's Dispose opens with the `Interlocked.Exchange` idempotency gate.
- Library awaits use `ConfigureAwait(false)`; every `TaskCompletionSource` uses
  `RunContinuationsAsynchronously`; fire-and-forget goes through a fault-observing helper.
- No new `.GetAwaiter().GetResult()` in product code outside the reviewed `SyncOverAsyncBaseline`
  (Dispose/transport paths); the ArchitectureTests gate enforces this and its baseline may only shrink.

## Sources

- ENGINEERING_RULES.md — K3 (Threading contracts), R5 (no silent catch), R6 (no sync-over-async baseline).
- docs/archive/agent-log/2026-07-08-dev-b4-threading.md — ICE sync-over-async removal, gate-enforced.
- docs/thread-memory-safety-analysis.md — race catalogue (StateChanged-outside-lock, unsynchronised
  session properties, handler-iteration snapshot, disposal-vs-event race) that this contract closes.
- Code (graphify-oriented, then read): `src/Core/Domain/Calls/Call.cs` (`TransitionTo`, `State`);
  `src/Core/Infrastructure/Sip/Adapters/SipCoreCallChannel.cs` (disposed-guard);
  `src/Core/Infrastructure/Sip/Transport/SipTransportRuntime.cs` (`.ToArray()` dispatch);
  `src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSessionContextAdapter.cs` (HARD-C1/C2);
  `src/Core/Application/Media/MediaConnection.cs` (idempotent Dispose);
  `src/Core/Application/Media/CallMediaOrchestrator.cs` (background media setup, fault-logged);
  `src/Core/Domain/Lines/PhoneLine.cs` (`ObserveHangupAsync`);
  `tests/CalloraVoipSdk.Core.IntegrationTests/SipActiveInviteStateConsistencyTests.cs`.
- Related ADRs: C14-02 (media-hot-path concurrency), C07-01 (allocation avoidance), C18-01 (media-tap contract).
