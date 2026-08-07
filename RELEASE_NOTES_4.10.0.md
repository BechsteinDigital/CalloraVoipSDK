# CalloraVoipSdk 4.10.0

**Per-call outgoing-audio mute on `ICall`.** The SDK already had a device-wide mute
(`IVoipClient.SetAudioInputMuted`), but it silences the capture device for *every* call at once — so it cannot
mute one call while another stays live. That is exactly the case a **contact-center agent** or a **multi-line
softphone** hits: put one caller on private mute while continuing to speak on a second, concurrent call.
4.10.0 adds a mute that lives on the call itself and gates only that call's outgoing audio.

The mute is **local** — it stops the SDK from sending this call's captured audio to the peer. Unlike hold it is
**not signalled**: there is no re-INVITE and no media-direction renegotiation, so the peer simply stops
receiving audio for this call. And unlike the device-wide mute it is **per call**, so on a client running
several concurrent calls each one mutes independently.

## New in 4.10.0

Two additive members on `ICall` (obtained from `line.DialAsync(...)`, `IncomingCallEventArgs.Call`, or the
`OnIncomingCall` hook):

- **`MuteAsync(bool muted, CancellationToken ct = default)`** — mutes (`true`) or resumes (`false`) this call's
  outgoing audio. Local only, so it is valid in any live state and does not throw for state; a no-op when the
  call is already in the requested state.
- **`IsMuted`** — reads the current outgoing-mute state (`false` by default).

```csharp
// Contact center: two concurrent calls, mute one while the other stays live.
ICall a = await line.DialAsync("sip:1001@pbx.example.com");
ICall b = await line.DialAsync("sip:1002@pbx.example.com");

await a.MuteAsync(true);        // 1001 no longer hears the agent…
bool muted = a.IsMuted;        // true
// …the agent keeps talking to 1002 (b is untouched), then resumes a:
await a.MuteAsync(false);
```

## Behaviour and limits

- **No public API change beyond the two additions.** `PublicApi.approved.txt` gains exactly the two `ICall`
  members; there is nothing to migrate and no on-wire behaviour change for a consumer that does not call them.
- **Outgoing / microphone mute, not speaker.** While muted no RTP is sent for this call's outgoing direction;
  **inbound audio is unaffected** — the agent still hears the caller.
- **Covers every outbound path.** The mute gates the single outbound audio choke point, so both the default
  microphone (via the media sender) and custom audio are muted.
- **Not hold, not device-wide mute.** Hold (`HoldAsync`) is *signalled* to the peer (a re-INVITE); this mute is
  a silent local send-gate. The device-wide `IVoipClient.SetAudioInputMuted` mutes the capture device for
  *every* call; this affects **only the one call**.
- **Scope: SIP calls (`ICall`).** The WebRTC `IPeerConnection` has its own track model and is not part of this
  change.

See [`CHANGELOG.md`](CHANGELOG.md) for the concise entry.
