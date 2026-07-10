# Lines

A **line** (`IPhoneLine`) is a registered SIP identity — the result of a successful
`ConnectAsync`. It is the origin of outbound calls and the target of inbound ones. The
`PhoneLineManager` (`client.Lines`) owns the registered lines.

## Getting a line

```csharp
var connect = await client.ConnectAsync(new SipAccount
{
    Username = "1001", Password = "secret", SipServer = "pbx.example.com"
});

IPhoneLine line = connect.Line!;   // valid only when connect.IsSuccess
```

`SipAccount` carries the credentials plus registration behaviour:

- `RegistrationExpiry` (default `300` s) — the REGISTER `Expires` value
- `InboundNumbers` — DIDs to match for trunk inbound
- `AcceptTrunkInbound` (default `true`)

## Placing calls

```csharp
ICall call = await line.DialAsync("sip:1002@pbx.example.com");
// optional per-call overrides:
ICall secure = await line.DialAsync("sip:1002@pbx.example.com",
    new DialOptions { UseSrtp = true });
```

`DialAsync` returns immediately with a [`ICall`](calls.md) in an early state; observe
`ICall.StateChanged` to follow ringing → connected. The client-level
`DialAndWaitUntilConnectedAsync` wraps this and awaits the answer.

## Line events

| Event | Fires when |
|-------|-----------|
| `StateChanged` | Registration state changes (registered / failed / expired) |
| `IncomingCall` | An INVITE arrives for this line |
| `LineReconnecting` | The SDK is re-establishing a dropped registration |
| `LineReconnectFailed` | A reconnect attempt failed |

All line events follow the [event threading contract](events.md).

## Unregistering

```csharp
await line.UnregisterAsync();
```

Unregister reuses the registration's Call-ID and next CSeq (RFC 3261 §10.2.2) so the
registrar actually clears the binding. `Dispose()` on the client unregisters all lines.
