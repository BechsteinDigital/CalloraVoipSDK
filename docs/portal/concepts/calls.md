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

## Why a call ended

Once a call reaches `Terminated`, `ICall.TerminationReason` explains **why** — so a busy, unanswered,
cancelled or rejected call is distinguishable from a generic failure. The same value is on the
terminating `StateChanged` event (`CallStateChangedEventArgs.TerminationReason`).

```csharp
call.StateChanged += (_, e) =>
{
    if (e.NewState != CallState.Terminated) return;
    var reason = e.TerminationReason;
    Console.WriteLine($"{reason?.Category} ({reason?.SipStatusCode} {reason?.ReasonPhrase}), " +
                      $"ended by {reason?.TerminatedBy}");
};
```

`CallTerminationReason` carries the `SipStatusCode` and `ReasonPhrase`, a protocol-neutral
`Category` (busy, no answer, cancelled, rejected, …), `TerminatedBy` (local or remote) and
`RetryAfterSeconds` where the peer supplied one. The classification follows the authoritative SIP
response status (RFC 3261 §21), not the advisory Q.850 `Reason` header — the same choice PJSIP,
SIPSorcery and Twilio make.

## Media

Each call has a media session. Attach the default audio device with
`client.AttachDefaultAudioAsync(call)`, or attach a [media tap](../guides/media-tap.md)
for bots and streaming. Negotiated encryption (SRTP/SRTCP) is transparent to this
surface — see [SRTP/SRTCP](../guides/srtp-srtcp.md).
