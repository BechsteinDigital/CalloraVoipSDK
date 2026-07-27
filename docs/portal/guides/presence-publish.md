# Presence & event state (SIP PUBLISH)

Publish event state — most commonly presence — with SIP `PUBLISH` (RFC 3903). A PUBLISH
sends an event-state document (for example a PIDF presence body) for your own
address-of-record to the event state compositor, so watchers can be notified of your
availability.

## Publish state

```csharp
string pidf = """
    <?xml version="1.0" encoding="UTF-8"?>
    <presence xmlns="urn:ietf:params:xml:ns:pidf" entity="sip:alice@example.com">
      <tuple id="a">
        <status><basic>open</basic></status>
      </tuple>
    </presence>
    """;

PublishResult result = await client.PublishAsync(
    eventType: "presence",
    body: pidf,
    contentType: "application/pidf+xml",
    expiresSeconds: 3600);

Console.WriteLine($"ETag {result.ETag}, granted {result.ExpiresSeconds}s");
```

`PublishAsync` sends the publication and completes when the compositor answers a `2xx`,
returning a `PublishResult`:

| Property | Meaning |
|---|---|
| `ETag` | The SIP-ETag the compositor assigned (or `null` if it sent none) |
| `ExpiresSeconds` | The **granted** lifetime in seconds (may be lower than you requested) |

It **faults** on a non-2xx final response or if no response arrives. Register a line first —
the state is published for that line's address-of-record — or use `line.PublishAsync(...)`
to publish from a specific line.

The `eventType` is the SIP event package (the `Event` header). `presence` is the common
case; the body's `contentType` must match the package (for presence, `application/pidf+xml`).

## Refresh, modify and remove

Per RFC 3903 a publication is **soft state**: it is kept alive by *refreshing* it before
`ExpiresSeconds` elapses, *modified* by publishing a new body, and *removed* early with
`Expires: 0` — each carrying the `SIP-If-Match` entity tag from the previous response.

Keep the `ETag` from the previous call and pass it to the matching method. Each one returns a
**new** ETag — always use the most recent one for the next operation.

```csharp
// Keep the publication alive without changing its state:
PublishResult refreshed = await client.RefreshPublicationAsync("presence", result.ETag!);

// Replace the published body (e.g. open -> busy):
PublishResult modified = await client.ModifyPublicationAsync(
    "presence", refreshed.ETag!, busyPidf, "application/pidf+xml");

// Withdraw the publication (sends Expires: 0):
await client.RemovePublicationAsync("presence", modified.ETag!);
```

All three exist on `IPhoneLine` too (`line.RefreshPublicationAsync(...)` etc.) when you need a
specific line rather than the first registered one. `RemovePublicationAsync` returns `Task` —
after a removal there is no publication left to tag.

Refresh before the granted `ExpiresSeconds` elapses; a lapsed publication cannot be refreshed
and must be re-established with `PublishAsync`.

## Notes

- **Own address-of-record only.** A UA publishes state for its own presentity; the
  Request-URI is the line's own AoR.
- **Threading.** These are normal awaitable calls; they do not run on a media or
  signaling callback thread.
- **Automatic refresh.** The SDK does not run a refresh loop for publications — schedule the
  refresh yourself from the granted `ExpiresSeconds`.

## See also

- [Instant messages (SIP MESSAGE)](messaging.md) — stateless pager-mode IM.
- [Handling inbound calls](inbound-calls.md) — `SUBSCRIBE`/`NOTIFY` and other event flows.
