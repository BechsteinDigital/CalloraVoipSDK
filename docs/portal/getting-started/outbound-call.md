# Minimal outbound call

Register a line, place a call, attach the default audio device, hang up. Every step
below maps to a real `VoipClient` method.

> **Runnable example:** [BasicCalling](https://github.com/BechsteinDigital/CalloraVoipSDK/tree/main/examples/CalloraVoipSdk.Sample.BasicCalling)
> — an interactive console softphone (dial, answer, hang up).

```csharp
using CalloraVoipSdk.Client;

using var client = new VoipClient(new SdkConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent     = "MySoftphone/1.0"
});

// 1. Register against the PBX / trunk.
var connect = await client.ConnectAsync(new SipAccount
{
    Username  = "1001",
    Password  = "secret",
    SipServer = "pbx.example.com"
});

if (!connect.IsSuccess || connect.Line is null)
    throw new InvalidOperationException($"Connect failed: {connect.Status}");

// 2. Dial and wait until the callee answers (early-media/ringing handled internally).
var dial = await client.DialAndWaitUntilConnectedAsync(
    connect.Line,
    "sip:1002@pbx.example.com");

if (!dial.IsSuccess || dial.Call is null)
    throw new InvalidOperationException($"Dial failed: {dial.Status}");

// 3. Route call audio to the default input/output device.
await client.AttachDefaultAudioAsync(dial.Call);

// 4. …talk…

// 5. End the call (sends BYE).
await dial.Call.HangupAsync();
```

## What each call does

- **`ConnectAsync(SipAccount)`** — registers the account and returns a `ConnectResult`
  carrying the `IPhoneLine` on success. Inspect `IsSuccess`/`Status` before using `Line`.
- **`DialAndWaitUntilConnectedAsync(line, uri)`** — sends the INVITE and completes once
  the dialog is confirmed (200 OK/ACK). For non-blocking dialing use
  `line.DialAsync(uri)` and observe `ICall.StateChanged` instead — see
  [Making calls](../guides/making-calls.md).
- **`AttachDefaultAudioAsync(call)`** — binds the OS default audio devices to the call's
  media session. Skip this for bot/streaming scenarios and use a
  [media tap](../guides/media-tap.md) instead.
- **`HangupAsync()`** — tears down the dialog with a BYE. `Dispose()` on the client also
  makes a best-effort hangup for any still-active call.

## Next

- [Minimal inbound call](inbound-call.md)
- [Making calls guide](../guides/making-calls.md) — DTMF, transfer, non-blocking dial
