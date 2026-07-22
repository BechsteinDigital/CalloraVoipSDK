# Media tap

A **media tap** attaches your code to a call's audio — pulling encoded frames out
(`IMediaReceiver`) and/or pushing frames in (`IMediaSender`). This is the foundation for
bots, transcription, and streaming to an AI backend, and it is the contract the
[commercial modules](../commercial/index.md) build on.

> **Runnable example:** [CustomAudio](https://github.com/BechsteinDigital/CalloraVoipSDK/tree/main/examples/CalloraVoipSdk.Sample.CustomAudio)
> attaches a receiver for inbound frame stats and a sender that injects a generated PCMU
> tone — no audio hardware.

## Receive call audio

```csharp
IMediaReceiver receiver = client.Media.CreateReceiver();
// attach the receiver to the call's media session, then consume the encoded frames
// (e.g. forward to STT, record, analyze) …
```

## Inject audio into a call

```csharp
IMediaSender sender = client.Media.CreateSender();
await sender.SendAsync(frame);   // frame: an encoded MediaFrame (payload in the negotiated codec)
```

`SendAsync(MediaFrame, CancellationToken)` paces frames into the call's send path. Feed it
frames **already encoded in the negotiated codec** — encode your TTS/PCM output first (the
payload is not PCM; see the note below).

## Typical bot loop

1. `CreateReceiver()` → inbound frames (encoded in the negotiated codec) → decode → your STT / logic.
2. Your logic produces a response as audio → encode it into the negotiated codec.
3. `CreateSender().SendAsync(frame)` → the response plays to the remote party.

SRTP/SRTCP is transparent — tapped frames are already decrypted — but the payload is
**encoded in the negotiated codec** (`MediaFrame.PayloadType`, e.g. 0 = PCMU), **not** PCM.
Decode/encode it yourself; the CustomAudio example µ-law-encodes a tone for PCMU.

## Headless operation

For a server-side bot you usually do **not** call `AttachDefaultAudioAsync`. Skip the
OS device entirely and drive the call through the tap. Set an
[`InboundMediaTimeout`](../concepts/voipclient.md#configuration) so calls without media
don't linger.

## Relationship to modules

The tap is a public, tested contract. Commercial plugins (Realtime, WebSocket,
Intelligence) consume exactly this surface via the
[module registry](../concepts/modules.md) — and so can your own in-house modules.
