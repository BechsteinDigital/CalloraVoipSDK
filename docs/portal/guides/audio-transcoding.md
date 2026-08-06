# Audio transcoding (PCM)

A server-side consumer can transcode between the SDK's payload codecs and linear PCM16 through a small,
transport-only surface in `CalloraVoipSdk.Audio.Abstractions.Processing`. The prototypical case is an
**SFU/mixer** that decodes a phone leg (G.711/G.722 over SIP) to PCM, mixes it into a **WebRTC**
conference, and re-encodes to Opus per leg. Concentus and NAudio stay behind the interface — they never
appear in a public signature, and no extra `PackageReference` is needed. See
[ADR-065](../adr/ADR-065-public-pcm-transcoding-surface.md).

## Surface

| Member | Signature | Meaning |
|--------|-----------|---------|
| `AudioPayloadCodecFactory.Create` | `Create(ActiveCodec codec)` | Transcoder at the codec's canonical PCM rate (48 kHz Opus / 16 kHz G.722 / 8 kHz G.711) |
| `AudioPayloadCodecFactory.Create` | `Create(ActiveCodec codec, int pcmSampleRate)` | Transcoder at an explicit PCM rate (Opus: 8/12/16/24/48 kHz) |
| `IAudioPayloadCodec.DecodeToPcm16` | `byte[] DecodeToPcm16(ReadOnlySpan<byte> payload)` | Decode one RTP payload to PCM16 LE bytes |
| `IAudioPayloadCodec.EncodeFromPcm16` | `byte[] EncodeFromPcm16(ReadOnlySpan<byte> pcm16)` | Encode PCM16 LE bytes to one RTP payload |
| `IAudioPayloadCodec.Codec` | `ActiveCodec` | The codec this instance transcodes |
| `IAudioPayloadCodec.PcmSampleRate` | `int` | PCM sample rate (media rate, **not** the RTP clock) |

`ActiveCodec` values: `Pcmu`, `Pcma`, `G722`, `Opus`. I/O is **PCM16 little-endian** (2 bytes per
sample). `IAudioPayloadCodec` is `IDisposable`.

## Decode a leg, then encode the mix

Create **one instance per stream direction** — Opus and G.722 carry inter-frame state, so a decode
instance and an encode instance are separate and never shared across directions or calls.

```csharp
using CalloraVoipSdk.Audio.Abstractions.Processing;

// Inbound phone leg (µ-law) → PCM
using var phoneDecoder = AudioPayloadCodecFactory.Create(ActiveCodec.Pcmu);

// Outbound conference leg (Opus) → payload
using var opusEncoder = AudioPayloadCodecFactory.Create(ActiveCodec.Opus);

// per received phone packet:
byte[] phonePcm = phoneDecoder.DecodeToPcm16(g711Payload);   // PCM16 LE @ 8 kHz

// … resample to 48 kHz and mix in your own buffer, producing `mixPcm` (PCM16 LE @ 48 kHz) …

byte[] opusPayload = opusEncoder.EncodeFromPcm16(mixPcm);    // send this to the WebRTC peer
```

The SDK does not resample or mix for you — it hands you PCM16 at `PcmSampleRate` and takes PCM16 back.
An empty input yields an empty array. For Opus, `EncodeFromPcm16` requires a valid Opus frame length
(2.5/5/10/20/40/60 ms at `PcmSampleRate`); an invalid length throws `ArgumentException`.

## Sample rate vs. RTP clock

`PcmSampleRate` is the rate of the PCM this instance decodes to / encodes from — **not** the RTP
timestamp clock. They differ for G.722: its PCM rate is 16 kHz while its RTP clock is 8 kHz
(RFC 3551 §4.5.2). For Opus the RTP clock is always 48 kHz (RFC 7587 §4.1) regardless of the PCM rate.
Never use `PcmSampleRate` as the RTP clock.

Fixed-rate codecs are fail-closed: G.711 and G.722 reject any `pcmSampleRate` other than their
canonical rate (8 kHz / 16 kHz) rather than producing silently mis-rated audio. Opus accepts
8/12/16/24/48 kHz and resamples internally.

```csharp
AudioPayloadCodecFactory.Create(ActiveCodec.G722, 8_000);   // throws ArgumentException — G.722 is 16 kHz
AudioPayloadCodecFactory.Create(ActiveCodec.Opus, 24_000);  // ok — Opus honours the supported rate
```

## Limitations

- **One instance per stream direction.** Opus and G.722 carry predictor/FEC state; sharing an instance
  across directions or calls produces artefacts. G.711 is stateless, but the same contract applies
  uniformly. Dispose each instance when the stream ends.
- **Transcoding only — no resampling or mixing.** You own the PCM buffer, the mix, and any rate
  conversion between codecs (e.g. 8 kHz phone PCM ↔ 48 kHz Opus PCM).
- The `Audio.*` assemblies sit outside the Client/Core `PublicApi.approved.txt` baseline by design and
  are governed by their own module contract. They ship transitively via the `CalloraVoipSdk`
  meta-package — no direct Concentus/NAudio dependency.

## See also

- [Media tap](media-tap.md) — observe/record decoded PCM on a live call
- [Audio devices](audio-devices.md) — capture/render endpoints
- [WebRTC](webrtc.md) — the forwarding peer primitives an SFU builds on
