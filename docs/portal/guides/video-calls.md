# Video calls

The SDK carries **encoded video** end to end over the public API, exactly like audio: it
negotiates the video m-line, moves reassembled frames in and out, and feeds back a
recommended bitrate and keyframe requests. It is **transport-only** — the SDK never encodes
or decodes. You bring the codec (VP8, H.264, …), the camera, and the display.

> **Runnable example:** [VideoCalling](https://github.com/BechsteinDigital/CalloraVoipSDK/tree/main/examples/CalloraVoipSdk.Sample.VideoCalling)
> wires a video call over nothing but the public API — receive, send, the bitrate hook, and a
> clearly marked `StubVideoEncoder` placeholder (no real codec).

## Two ways to use it

1. **Raw frame tap** — you already own an encoder/decoder. Push and pull `VideoFrame`s
   directly through `IVideoSender`/`IVideoReceiver`. Full control, no bundled codec.
2. **Default video convenience** — hand the SDK an `IVideoDevice` (your codec package) and
   call `AttachDefaultVideoAsync`; the SDK owns the wiring. The "audio-simple" path.

## Receive inbound video

```csharp
IVideoReceiver receiver = client.Media.CreateVideoReceiver();
receiver.AttachToCall(call);

receiver.FrameReceived += (_, e) =>
{
    VideoFrame frame = e.Frame;      // encoded bytes — decode with your own codec
    // frame.Payload      : ReadOnlyMemory<byte> (one coded picture)
    // frame.PayloadType  : negotiated RTP payload type (identifies the codec)
    // frame.RtpTimestamp : 90 kHz video clock
    // frame.IsKeyFrame   : true for an intra-coded key frame
    myDecoder.Decode(frame.Payload);
};
```

The `FrameReceived` handler runs **synchronously on the media receive path** — do not block
or perform inline I/O. Buffer into your own queue and return immediately.

## Send outbound video

```csharp
IVideoSender sender = client.Media.CreateVideoSender();
sender.AttachToCall(call);

// payload must already be encoded in the negotiated codec
await sender.SendAsync(new VideoFrame(
    Payload: encodedBytes,
    PayloadType: 96,
    RtpTimestamp: rtpTs,
    IsKeyFrame: false));
```

Frames sent while the call is not `Connected` or `OnHold` are dropped.

## Adapt to the network — the payoff

The SDK folds transport-cc feedback into a ready-to-use recommended bitrate. Set your
encoder to it; don't compute bandwidth yourself.

```csharp
sender.RecommendedBitrateChanged += (_, e) => encoder.SetBitrate(e.RecommendedBitrateBps);

// or poll:
long? bps = sender.RecommendedBitrateBps;      // null until congestion control is active
NetworkQuality? quality = sender.NetworkQuality; // Good / Fair / Poor
```

## Keyframes

```csharp
// The peer asked for a fresh reference frame (RTCP PLI/FIR):
sender.KeyFrameRequested += (_, _) => encoder.ForceKeyFrame();
```

Send the resulting intra frame via `SendAsync` with `IsKeyFrame: true`. On the receive side
the SDK already reports inbound losses to the peer (Generic NACK + throttled PLI, RFC 4585)
when the peer advertised that feedback in SDP — you do not drive that.

## Default video convenience

If you package your codec behind an `IVideoDevice`, register it in DI and let the SDK wire
the call for you — the byte-analog of `AttachDefaultAudioAsync`:

```csharp
services.AddCalloraVoip(options => { /* … */ });
services.AddSingleton<IVideoDevice, MyVp8VideoDevice>();   // your codec package
// …
await client.AttachDefaultVideoAsync(call);   // connects your device on Connected/OnHold
await client.DetachDefaultVideoAsync(call);
```

Without a registered `IVideoDevice`, `AttachDefaultVideoAsync` fails closed with
`InvalidOperationException` — the SDK core ships no codec. DI registration is the supported
way to supply the device.

`IVideoDevice.Connect(receiver, sender, parameters)` receives the negotiated codec via
`parameters` (`VideoConnectionParameters`: payload type, codec name, clock rate), so the
device encodes/decodes in the agreed format. The negotiated video parameters are also
readable on `ICall.MediaParameters.Video` (`CallVideoParameters`).

## What the SDK does and does not do

| The SDK | Your code |
|---------|-----------|
| Negotiates the video m-line (SDP) | — |
| Packetizes / depacketizes RTP (VP8, H.264) | — |
| Moves encoded frames in and out | Encode / decode the payload |
| SRTP/SRTCP, RTX, NACK/PLI feedback | — |
| Recommends a bitrate, surfaces keyframe requests | Set the encoder, capture, render |

The SDK **never encodes or decodes**. A `VideoFrame` payload is encoded codec bytes, not
pixels. FIR is honoured on receive but not generated (PLI is the keyframe request for
point-to-point calls).

## Relationship to audio

Video mirrors the audio contract: `CreateVideoReceiver()/CreateVideoSender()` parallel
`CreateReceiver()/CreateSender()`, and `AttachDefaultVideoAsync` parallels
`AttachDefaultAudioAsync`. The difference is that the SDK bundles audio codecs and OS audio
devices, while video is transport-only — the codec lives in your `IVideoDevice`.
