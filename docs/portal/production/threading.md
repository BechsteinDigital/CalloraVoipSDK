# Threading

The SDK is thread-safe by design: you may call its methods from any thread. The two
things you must respect are the **event threading contract** and the **error contract**.

## Event threading contract

Events fire on SDK-internal threads. **Handlers must not block and must not throw.** For
the per-event thread table see [Events](../concepts/events.md); the essentials:

- `StateChanged` / `HoldStateChanged` — signaling thread, serialized.
- `DtmfReceived` — **two possible threads** (SIP INFO or the RFC-4733 media path); make
  the handler thread-safe.
- `TransferRequested` — signaling thread, handled **synchronously** (accept/reject inline).
- `QualitySnapshotChanged` — media/RTCP thread.

Marshal to your own context/queue for anything non-trivial. A blocking handler stalls the
pipeline it runs on.

## Error contract

Call-control methods split into two deliberate categories:

**Throwing methods** — a wrong-state call is a programmer error and throws
`InvalidOperationException` (via internal state guards):

```
AcceptAsync, HangupAsync, HoldAsync, UnholdAsync,
SendDtmfAsync, BlindTransferAsync, AttendedTransferAsync
```

**`CallActionResult`-returning methods** — foreseeable outcomes (remote decline, invalid
request, timeout, wrong state at the peer) are returned, not thrown:

```
RejectAsync, RedirectAsync, SendInfoAsync, SendOptionsAsync,
SendSubscribeAsync, SendNotifyAsync
```

Inspect the `CallActionResult` (Canceled / InvalidRequest / InvalidState / Failed) instead
of wrapping these in try/catch. Reserve try/catch for the throwing set and for genuinely
exceptional infrastructure failures.

## Cancellation

Async methods accept a `CancellationToken`. Cancellation cooperatively aborts the awaited
operation where supported; some fire-and-forget teardown paths run to completion by design
so the far end is notified.

## Concurrency guidance

- One `VoipClient` handles many concurrent lines/calls; you do not need one client per
  call.
- Guard your own per-call state — event handlers for different calls run concurrently.
- `MaxConcurrentCallsPerLine` (default 10) caps fan-out per line.
