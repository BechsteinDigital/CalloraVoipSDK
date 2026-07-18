# Media

The media layer moves audio between calls, devices and your code. `client.Media`
(`MediaManager`) is the factory for every media primitive.

## Primitives

```csharp
IMediaReceiver receiver = client.Media.CreateReceiver();  // pull encoded frames out
IMediaSender   sender   = client.Media.CreateSender();    // push frames into a call
MediaConnector connector = client.Media.CreateConnector(); // bridge two calls
```

- **`IMediaReceiver`** — receives encoded `MediaFrame`s (payload in the negotiated codec) from a call (for transcription,
  streaming to an AI backend, custom mixing).
- **`IMediaSender`** — `SendAsync(MediaFrame)` injects audio into a call (TTS, prompts,
  synthesized speech).
- **`MediaConnector`** — cross-connects two active calls into a bridge.

Attaching these to a call is the [media tap](../guides/media-tap.md); bridging is the
[bridge guide](../guides/bridge-calls.md).

## Video

Video has its own transport-only primitives — the SDK moves encoded frames but never
encodes or decodes:

```csharp
IVideoReceiver videoIn  = client.Media.CreateVideoReceiver(); // inbound encoded VideoFrames
IVideoSender   videoOut = client.Media.CreateVideoSender();   // outbound encoded VideoFrames
```

`IVideoSender` also surfaces the SDK's recommended outbound bitrate and peer keyframe
requests so you can drive your encoder. Bring your own codec (VP8, H.264, …). Full walkthrough:
[Video calls](../guides/video-calls.md).

## Default audio device

For a plain softphone you usually skip the primitives and let the SDK wire the OS
device:

```csharp
await client.AttachDefaultAudioAsync(call);
await client.DetachDefaultAudioAsync(call);
```

Runtime device switching, mute and volume live on the client — see
[Audio devices](../guides/audio-devices.md).

## Recording and playback

```csharp
IRecordingSession rec = await client.Media.StartCallRecordingAsync(call, "call.wav");
await rec.PauseAsync(); await rec.ResumeAsync(); await rec.StopAsync();

IPlaybackSession play = await client.Media.StartCallPlaybackAsync(call, "prompt.wav");
await play.StopAsync();
```

Conference variants (`StartConferenceRecordingAsync` / `StartConferencePlaybackAsync`)
operate on a bridge. Details: [Recording/playback](../guides/recording-playback.md).

## Codecs and encryption

Codec preference is set once via `VoipConfiguration.PreferredAudioCodecs`; SRTP/SRTCP is
negotiated automatically per `SrtpPolicy`. Neither changes the media-primitive API — you
work with `MediaFrame`s whose payload is encoded in the negotiated codec (decode/encode it
yourself); SRTP/SRTCP and transport stay transparent.
