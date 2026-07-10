# Events

The SDK raises events for call, line and quality changes. Consuming them correctly means
knowing **which thread** each fires on and what a handler is allowed to do.

## Threading contract

Handlers run on SDK-internal threads. **A handler must not block and must not throw.**
A blocking handler stalls the pipeline it runs on; a throwing handler is logged and
swallowed but may drop the notification.

| Event | Source | Thread |
|-------|--------|--------|
| `ICall.StateChanged` | call | Signaling thread, buffered/serialized |
| `ICall.HoldStateChanged` | call | Signaling thread, buffered/serialized |
| `ICall.DtmfReceived` | call | **Two possible threads**: SIP INFO handling *or* the RFC-4733 media path |
| `ICall.TransferRequested` | call | Signaling thread — handled **synchronously** (accept/reject inline) |
| `ICall.QualitySnapshotChanged` | call | Media/RTCP thread |
| `IPhoneLine.StateChanged` | line | Signaling thread, buffered |
| `IPhoneLine.IncomingCall` | line | Signaling thread |
| `VoipClient.IncomingCall` / `CallStateChanged` | client | Signaling thread |

Because `DtmfReceived` can arrive from two different threads, make its handler
thread-safe or marshal onto your own queue.

## Do / don't

- **Do** marshal to your UI/synchronization context, enqueue work, or set flags.
- **Do** keep handlers short and non-blocking.
- **Don't** run `await`-heavy work inline on the event thread — offload it.
- **Don't** throw; catch inside your handler.

## Convenience subscription

`client.OnIncomingCall(handler)` returns an `IDisposable`; dispose it to unsubscribe.
This is the recommended way to handle inbound calls without wiring raw events.

The same contract is documented on the types themselves (`ICall`, `IPhoneLine`,
`VoipClient`) via XML docs, and expanded under
[Production → Threading](../production/threading.md).
