# Calls

A **call** (`ICall`) is one SIP dialog and its media session. You obtain one from
`line.DialAsync(...)` (outbound) or the `IncomingCall`/`OnIncomingCall` surface
(inbound). `client.Calls` (`CallManager`) tracks the active set.

## Control surface

Methods that **throw** `InvalidOperationException` when the call is in the wrong state
(they represent a programmer error if misused):

```csharp
await call.AcceptAsync();                 // answer an inbound call (200 OK)
await call.HangupAsync();                 // end the call (BYE)
await call.HoldAsync();                   // re-INVITE, sendonly
await call.UnholdAsync();                 // re-INVITE, sendrecv
await call.SendDtmfAsync(DtmfTone.Five);  // RFC 4733 telephone-event
await call.BlindTransferAsync("sip:1003@pbx.example.com");
bool ok = await call.AttendedTransferAsync(consultationCall);
```

> `AttendedTransferAsync` is the exception to the rule above: it currently has **no** state
> guard, so a wrong-state call is not rejected — invoke it only from `Connected`.

Methods that return a `CallActionResult` instead of throwing for foreseeable outcomes
(remote decline, invalid request, timeout):

```csharp
CallActionResult r = await call.RejectAsync(/* … */);
await call.RedirectAsync(/* … */);
await call.SendInfoAsync(/* … */);
await call.SendOptionsAsync();
await call.SendSubscribeAsync(/* … */);
await call.SendNotifyAsync(/* … */);
```

The full rule is the [error contract](../production/threading.md#error-contract).

## Call events

| Event | Meaning | Thread |
|-------|---------|--------|
| `StateChanged` | Dialog state transition | Signaling (serialized) |
| `HoldStateChanged` | Local/remote hold state | Signaling (serialized) |
| `DtmfReceived` | Inbound DTMF | SIP INFO **or** RFC-4733 media thread |
| `TransferRequested` | Peer asks for a transfer (REFER) | Signaling (synchronous accept/reject) |
| `QualitySnapshotChanged` | New RTCP quality snapshot | Media/RTCP thread |

See [Events](events.md) for the threading contract these follow. Subscribe **before** placing or
accepting a call — events are delivered live to the handlers registered at the time of the
transition and are not guaranteed to be replayed to a handler that subscribes afterwards.

## Media

Each call has a media session. Attach the default audio device with
`client.AttachDefaultAudioAsync(call)`, or attach a [media tap](../guides/media-tap.md)
for bots and streaming. Negotiated encryption (SRTP/SRTCP) is transparent to this
surface — see [SRTP/SRTCP](../guides/srtp-srtcp.md).
