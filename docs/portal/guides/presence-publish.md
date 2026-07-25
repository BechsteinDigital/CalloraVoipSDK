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

## Refresh, modify and remove — current limitation

Per RFC 3903 a publication is soft state: it is kept alive by **refreshing** it before
`ExpiresSeconds` elapses, **modified** by publishing a new body, and **removed** early with
`Expires: 0` — each carrying the `SIP-If-Match` entity tag from the previous response.

The SIP layer implements this full lifecycle, but a **public API to supply the entity tag is
not yet exposed**: today every `PublishAsync` call sends an *initial* publication (its own new
ETag). An app therefore cannot yet refresh, modify or remove a specific publication from the
facade — each publication simply expires after its granted lifetime.

> Tracked in [#76](https://github.com/BechsteinDigital/callora-voip-sdk/issues/76). Keep the
> returned `ETag` if you want to drive that lifecycle once the public entry point lands.

Until then, if you need presence to persist, re-publish before the granted lifetime expires
(accepting that each re-publish is a fresh publication rather than a true refresh).

## Notes

- **Own address-of-record only.** A UA publishes state for its own presentity; the
  Request-URI is the line's own AoR.
- **Threading.** `PublishAsync` is a normal awaitable call; it does not run on a media or
  signaling callback thread.

## See also

- [Instant messages (SIP MESSAGE)](messaging.md) — stateless pager-mode IM.
- [Handling inbound calls](inbound-calls.md) — `SUBSCRIBE`/`NOTIFY` and other event flows.
