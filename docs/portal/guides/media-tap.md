# Media tap

A **media tap** attaches your code to a call's audio — pulling decoded frames out
(`IMediaReceiver`) and/or pushing frames in (`IMediaSender`). This is the foundation for
bots, transcription, and streaming to an AI backend, and it is the contract the
[commercial modules](../commercial/index.md) build on.

## Receive call audio

```csharp
IMediaReceiver receiver = client.Media.CreateReceiver();
// attach the receiver to the call's media session, then consume decoded frames
// (e.g. forward to STT, record, analyze) …
```

## Inject audio into a call

```csharp
IMediaSender sender = client.Media.CreateSender();
await sender.SendAsync(frame);   // frame: a decoded MediaFrame (e.g. TTS output)
```

`SendAsync(MediaFrame, CancellationToken)` paces frames into the call's send path. Feed
it PCM produced by your TTS or audio source.

## Typical bot loop

1. `CreateReceiver()` → decoded inbound audio → your STT / logic.
2. Your logic produces a response as audio.
3. `CreateSender().SendAsync(frame)` → the response plays to the remote party.

Because you work with decoded frames, negotiated SRTP/SRTCP and the wire codec are
transparent — the tap sees plain PCM either way.

## Headless operation

For a server-side bot you usually do **not** call `AttachDefaultAudioAsync`. Skip the
OS device entirely and drive the call through the tap. Set an
[`InboundMediaTimeout`](../concepts/voipclient.md#configuration) so calls without media
don't linger.

## Relationship to modules

The tap is a public, tested contract. Commercial plugins (Realtime, WebSocket,
Intelligence) consume exactly this surface via the
[module registry](../concepts/modules.md) — and so can your own in-house modules.
