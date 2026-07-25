# Instant messages (SIP MESSAGE)

Send and receive out-of-dialog SIP `MESSAGE` requests (RFC 3428 pager-mode instant
messaging). MESSAGE is **stateless** — it opens no call or dialog; each message is a
standalone request. You can send and receive messages whenever a line is registered,
with no call in progress.

## Send a message

```csharp
// From the first registered line:
await client.SendMessageAsync("sip:bob@example.com", "Hello from Callora");

// Or from a specific line, with an explicit content type:
await line.SendMessageAsync("sip:bob@example.com", "Hello", contentType: "text/plain");
```

`SendMessageAsync` completes when the peer answers a `2xx`; it **faults** on a non-2xx
final response or if no response arrives (wrap it in `try/catch` to surface delivery
failures). Register a line first — the message is sent from that line's identity.

## Receive messages

```csharp
client.IncomingMessage += (sender, e) =>
{
    SipInstantMessage msg = e.Message;
    Console.WriteLine($"{msg.From} -> {msg.To}: {msg.Body} ({msg.ContentType})");
};
```

The SDK has already answered the incoming MESSAGE `200 OK` **before** the event fires —
the handler only consumes the content. `SipInstantMessage` is an immutable value object:

| Property | Meaning |
|---|---|
| `From` | Sender address (the MESSAGE `From` header) |
| `To` | Recipient the message was addressed to (the `To` header) |
| `Body` | The message text/content |
| `ContentType` | MIME type of `Body` (default `text/plain`) |
| `CallId` | `Call-ID` of the request — a correlation token only; no dialog is created |

## Notes

- **Out-of-dialog.** MESSAGE is not tied to a call; it needs only a registered line.
- **Content type.** The default is `text/plain`; set `contentType` for other payloads
  (for example `application/im-iscomposing+xml` for typing indicators).
- **Best-effort delivery.** Per RFC 3428 a `2xx` means the next hop accepted the message,
  not that a human read it. There is no built-in delivery receipt.
- **Threading.** The `IncomingMessage` handler runs on a SIP signaling thread — keep it
  short and hand heavy work to your own queue.

## See also

- [Presence & event state (SIP PUBLISH)](presence-publish.md) — publish presence/event state.
- [Handling inbound calls](inbound-calls.md) — the parallel `IncomingCall` event.
