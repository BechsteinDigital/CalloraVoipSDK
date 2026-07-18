# Handling inbound calls

Inbound calls arrive as INVITEs on a registered [line](../concepts/lines.md). There are
two ways to observe them.

## Convenience hook (recommended)

```csharp
using var subscription = client.OnIncomingCall(async call =>
{
    // Decide per call:
    await call.AcceptAsync();                 // answer
    await client.AttachDefaultAudioAsync(call);
});
```

Dispose the returned `IDisposable` to stop handling.

## Raw events

```csharp
client.IncomingCall += (_, e) => { /* e.Call */ };
// or per line:
line.IncomingCall += (_, e) => { /* … */ };
```

Both fire on the signaling thread — keep the handler non-blocking
(see [Events](../concepts/events.md)).

## Accept, reject, redirect

```csharp
await call.AcceptAsync();               // 200 OK
await call.RejectAsync(/* reason */);   // decline (CallActionResult)
await call.RedirectAsync(/* target */); // 3xx (CallActionResult)
```

`AcceptAsync` throws if the call is not acceptable; `RejectAsync`/`RedirectAsync` return
a `CallActionResult` for foreseeable outcomes.

## Guarding inbound media

Set `VoipConfiguration.InboundMediaTimeout` (default 15 s) so answered calls that never
produce media are torn down instead of lingering. For held calls that go silent,
`HangupHeldCallOnMediaSilence` ends them automatically.

## Trunk inbound

For calls addressed to DIDs rather than your AOR, list the numbers on the account:

```csharp
new SipAccount
{
    Username = "trunkuser", Password = "secret", SipServer = "trunk.example",
    InboundNumbers = new[] { "4930123456", "4930123457" },
    AcceptTrunkInbound = true
};
```

See [NAT and SIP trunks](nat-and-trunks.md) and the [interop matrix](../interop/matrix.md).
