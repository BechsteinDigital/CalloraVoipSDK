# Lifecycle & dispose

Own the `VoipClient` for the life of your application (or per tenant) and dispose it
exactly once on shutdown.

## Construction

```csharp
using var client = new VoipClient(new SdkConfiguration { /* … */ });
```

`using` (or an explicit `Dispose()`) guarantees teardown. The client is not meant to be
constructed per call — construct once and reuse.

## What `Dispose()` does

- **Unregisters** every registered [line](../concepts/lines.md) (reusing each
  registration's Call-ID and next CSeq per RFC 3261 §10.2.2, so the registrar actually
  clears the binding).
- Makes a **best-effort BYE** for still-active calls. A BYE that faults during teardown is
  observed and logged rather than dropped as an unobserved task exception.
- Releases audio devices and media sessions.

## Graceful shutdown

For a clean shutdown, prefer to end work explicitly before disposing:

```csharp
foreach (var call in activeCalls)
    await call.HangupAsync();      // BYE, awaited

await line.UnregisterAsync();      // awaited unregister

client.Dispose();                  // best-effort cleanup for anything remaining
```

Explicit `HangupAsync`/`UnregisterAsync` are awaitable and deterministic; the best-effort
cleanup in `Dispose()` is a safety net, not a substitute for orderly teardown — on a
process exit racing the network, an awaited unregister is more reliable than relying on
dispose alone.

## Hosting

In a hosted/DI application, tie the client's lifetime to the host and dispose it in the
shutdown path (e.g. a hosted service's `StopAsync`, or the DI container disposing a
singleton). Do not let the process exit abruptly while calls or registrations are live if
you need clean teardown on the far end.
