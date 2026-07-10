# Making calls

This guide covers outbound calling beyond the [minimal example](../getting-started/outbound-call.md):
non-blocking dial, following call state, DTMF and transfers.

## Blocking vs. non-blocking dial

**Blocking** — wait until the callee answers:

```csharp
var dial = await client.DialAndWaitUntilConnectedAsync(line, "sip:1002@pbx.example.com");
if (dial.IsSuccess) await client.AttachDefaultAudioAsync(dial.Call!);
```

**Non-blocking** — get the call immediately and follow its state:

```csharp
ICall call = await line.DialAsync("sip:1002@pbx.example.com");
call.StateChanged += (_, e) =>
{
    // marshal off the signaling thread — see the events contract
    Console.WriteLine($"call {call} -> {e.State}");
};
```

## DTMF

```csharp
await call.SendDtmfAsync(DtmfTone.One);
await call.SendDtmfAsync(DtmfTone.Hash);
```

DTMF is sent as RFC 4733 telephone-events. Inbound DTMF surfaces via
`DtmfReceived` — note it can arrive on the SIP-INFO **or** the media thread
(see [Events](../concepts/events.md)).

## Hold / unhold

```csharp
await call.HoldAsync();     // re-INVITE sendonly
await call.UnholdAsync();   // re-INVITE sendrecv
```

Hold/unhold on an SRTP call keeps the media secured and rekeys as needed.

## Transfer

```csharp
// Blind: hand the call off, no consultation.
await call.BlindTransferAsync("sip:1003@pbx.example.com");

// Attended: consult first on a second call, then connect the two.
ICall consult = await line.DialAsync("sip:1003@pbx.example.com");
// …speak to 1003…
bool ok = await call.AttendedTransferAsync(consult);
```

Inbound transfer requests (REFER from the peer) arrive as `TransferRequested`, handled
synchronously so you can accept or reject inline.

## Error handling

`AcceptAsync`/`HangupAsync`/`HoldAsync`/`UnholdAsync`/`SendDtmfAsync`/transfer throw
`InvalidOperationException` if called in the wrong state. The `Send*`/`Reject`/`Redirect`
family returns `CallActionResult`. Full rule: [error contract](../production/threading.md#error-contract).
