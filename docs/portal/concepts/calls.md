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

### Mute this call's outgoing audio

`MuteAsync` gates **this call's** outgoing audio locally — the SDK stops (or resumes) sending its
captured audio to the peer:

```csharp
await call.MuteAsync(true);   // stop sending this call's audio to the peer
bool muted = call.IsMuted;   // true
await call.MuteAsync(false);  // resume
```

Unlike `HoldAsync` it is **not signalled** to the peer (no re-INVITE), and unlike the device-wide
`IVoipClient.SetAudioInputMuted` — which mutes the capture device for *every* call — it affects
**only this call**, so concurrent calls mute independently. It is **outgoing** (microphone) mute:
inbound audio is unaffected. Being local, it is valid in any live state and does not throw for state;
it is a no-op when the call is already in the requested state.

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

## Inbound caller and dialed identity

An inbound call surfaces both who is calling and which number they dialed, parsed once by the SDK so you do
not re-parse SIP headers yourself. All four properties are read-only and `null` on outbound calls (and when
the data is absent):

| Property | Meaning |
|----------|---------|
| `CalledNumber` | The dialed number (DID) the call was addressed to — the `To`/Request-URI user part, i.e. the number that selected the receiving trunk line. |
| `LocalParty` | The local party's SIP URI in this dialog, parallel to `RemoteParty` (on inbound the called/DID URI). |
| `RemoteNumber` | The caller's number — the user part of `RemoteParty`. |
| `RemoteDisplayName` | The caller's display name from the inbound `From` header (RFC 3261 §8.1.1.3), or `null` when none was sent. |

Typical use is a **screen-pop** and **trunk DID routing** — show the caller and branch on the DID the call
arrived on:

```csharp
client.OnIncomingCall(async call =>
{
    ScreenPop(
        dialedDid:   call.CalledNumber,      // route by which DID / line the call came in on
        callerNumber: call.RemoteNumber,
        callerName:  call.RemoteDisplayName);

    await call.AcceptAsync();
});
```

These are informational identity as received. For a trust-gated caller identity use `RemoteAssertedIdentity`
(P-Asserted-Identity, RFC 3325).

## Media

Each call has a media session. Attach the default audio device with
`client.AttachDefaultAudioAsync(call)`, or attach a [media tap](../guides/media-tap.md)
for bots and streaming. Negotiated encryption (SRTP/SRTCP) is transparent to this
surface — see [SRTP/SRTCP](../guides/srtp-srtcp.md).
