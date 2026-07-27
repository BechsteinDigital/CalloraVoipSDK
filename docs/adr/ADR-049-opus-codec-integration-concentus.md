# ADR-049: Opus Audio Codec Integration via Concentus (Managed Encode/Decode)

Status: Accepted
Date: 2026-07-08

## Context

The SDK ships a full audio media path (G.711 µ-law/A-law, G.722, comfort noise) that is
otherwise **transport-oriented**: the stack negotiates payload types, packetises, and moves RTP
without owning a compute-heavy codec runtime. The video path deliberately holds that line — video
is transport-only, with no native encode/decode inside the SDK (see the C11/C12 media clusters and
project memory: "Codec Transport-only (kein nativer Encode/Decode)").

Opus (RFC 7587) is different. Founders wanted Opus on the wire (sipgate lists Opus), and Opus is a
computed codec: there is no way to interoperate by moving payload bytes alone. Two options existed:
a native Opus binary (per-platform `.so`/`.dll`/`.dylib` deployment burden on every SDK consumer),
or a managed implementation. The founder decision recorded in the delivery log was **Concentus**
(pure C#, MIT), explicitly "kein natives Deployment" — accept a real runtime codec dependency in
Core in exchange for zero native-deployment cost.

This is the first (and, for audio, currently the only) place the SDK performs genuine
sample-level audio compression/decompression, which is why it warrants its own decision record: it
breaks the otherwise-uniform transport-only codec posture, on purpose, for one codec.

### Verified current state (graphify-grounded)

- **Concentus is a real Core runtime dependency**: `Concentus` 2.2.2 in
  `src/Core/CalloraVoipSdk.Core.csproj` (the first runtime codec dependency in Core).
- **`OpusPayloadCodec` does actual encode/decode, not transport shuffling.** It constructs a
  Concentus `IOpusEncoder`/`IOpusDecoder` (`OpusCodecFactory.CreateEncoder/CreateDecoder`, mono,
  `OPUS_APPLICATION_VOIP`) and calls `_encoder.Encode(...)` / `_decoder.Decode(...)` to convert
  between PCM16 and Opus payloads (`OpusPayloadCodec.cs:37-78`). Encoder/decoder are **stateful
  across frames** (prediction/FEC) → one instance per stream direction, not shared across calls.
- **RFC 7587 clock model is honoured in code**: `RtpClockRate = 48000` always, independent of the
  coded bandwidth; `SamplesPerDefaultFrame = 960` (20 ms). The PCM sample rate is a ctor parameter
  (48 kHz default; the bridge path passes 8 kHz — Concentus resamples internally), while the RTP
  timestamp clock stays 48 kHz (owned by the RTP session, not the codec).
- **Opt-in only on SDP.** Opus is an `OptInCodecs` entry (PT 107, `opus/48000/2`, dynamic PT) in
  `SdpUtilities.cs:34-36`; it is offered **only** when `SdkConfiguration.PreferredAudioCodecs`
  requests it. The default codec set is byte-unchanged, so the existing PCMU agent path is
  unaffected. `PayloadCodecKind.Opus = 7` is the normalized family; the string "OPUS" resolves to
  it in `AudioPayloadTranscoder.cs:327-328`, and the transcoding plan holds the stateful instance
  per call leg (`AudioPayloadTranscoder.cs:233`).
- **Two additional consumers exist beyond the file/transcode path** (divergence vs. what the O1/O2
  logs describe): `OpusDeviceCodec` wraps one `OpusPayloadCodec` for platform audio backends,
  adding a capture-side accumulator that emits whole 20 ms frames (`OpusDeviceCodec.cs`); and
  `BridgeAudioTranscoder` reuses `OpusPayloadCodec` at 8 kHz (its own decision — see the C13
  bridge ADR).
- **Negotiation guard**: negotiation requires ≥1 real audio codec; a pinned opt-in codec the peer
  does not offer yields a 488 rather than a telephone-event-only answer (per O1 log; enforced in
  the SDP negotiation path).

## Decision

Integrate Opus as a **first-class, managed, encode/decode codec** rather than treating it as
transport-only:

1. **Take a managed Opus dependency (Concentus, MIT) in Core.** Accept the first runtime codec
   dependency in the Core library in exchange for zero native-binary deployment for SDK consumers.
2. **Wrap it in `OpusPayloadCodec`** — a thin, stateful, mono, RFC 7587 adapter (48 kHz RTP clock;
   configurable PCM rate) that owns exactly one encoder + one decoder per stream direction.
3. **Expose Opus opt-in only** via `SdkConfiguration.PreferredAudioCodecs` (SDP PT 107,
   `opus/48000/2`), leaving the default codec set and the existing PCMU path byte-identical.
4. Resolve/normalise the codec through the existing `PayloadCodecKind` / `AudioPayloadTranscoder`
   plan machinery, holding the stateful codec instance per call leg.

### Crux

The C13 crux is a **deliberate asymmetry**: video stays transport-only (no native codec in the
SDK), but Opus does **real sample-level encode/decode in-process**. Verified: `OpusPayloadCodec`
calls Concentus `Encode()`/`Decode()` — it is not payload transport plus a bridge to device audio.
The reason Opus is the exception and video is not: Opus interop is impossible without computing the
codec, and a *managed* implementation (Concentus) removes the only real cost of doing so in an SDK
(native per-platform deployment). Video's compute cost and hardware-acceleration expectations do
not have an equivalent cheap managed answer, so video keeps the transport-only line.

## Consequences

Positive:
- Opus interoperability on the wire (sipgate, browsers) without shipping native binaries — SDK
  consumers stay platform-agnostic.
- Additive: default codec negotiation and the PCMU path are byte-unchanged; Opus is strictly
  opt-in, so the change cannot regress existing audio calls.
- One reusable codec adapter serves the file/transcode path, the device path
  (`OpusDeviceCodec`), and the bridge path (`BridgeAudioTranscoder`) at different sample rates.

Tradeoffs / honest divergence:
- **Codec posture is no longer uniform.** The SDK now has exactly one runtime audio codec that
  computes samples; the "transport-only" description that still fits video does **not** fit Opus.
  This ADR is the record that the asymmetry is intentional, not drift.
- **Managed-codec quality/performance ceiling.** Concentus is pure C#; it carries the CPU cost of a
  managed Opus and cannot use platform hardware codecs. Acceptable for VoIP frame sizes, but it is
  a ceiling, not free.
- **Tuning is defaulted, not exposed.** Bitrate, DTX, and decode-side FEC/PLC are Concentus
  defaults; none are configurable yet (O1/O2 caveats). Opus PLC/FEC is not used on loss.
- **Scope divergence from the logs**: `OpusDeviceCodec` (platform-backend adapter) is present in
  the code but not described in the O1/O2 delivery logs — it is a later reuse of `OpusPayloadCodec`
  and inherits the same managed encode/decode decision.
- **Not production-proven.** The O1 log is explicit: no "Opus produktionsbewiesen" claim; a real
  sipgate call needs the bridge (see C13 bridge ADR) and founder acceptance of audio quality.

## Guardrails

- Opus stays **opt-in only**; the default codec set and PCMU passthrough path remain byte-identical
  (regression-tested: default answer is Opus-free; PCMU preference yields PT 0/8000/160).
- One `OpusPayloadCodec` (encoder+decoder) instance per stream direction — never shared across
  calls or directions (state is prediction/FEC-bearing).
- RTP timestamp clock stays 48 kHz regardless of the codec's PCM sample rate; the codec never owns
  the RTP clock.
- Negotiation must still yield ≥1 real audio codec; a pinned unsupported opt-in codec → 488, never
  a telephone-event-only answer.
- No "production-ready Opus" claim without a real interop test (sipgate) plus quality acceptance;
  the codec being green in unit tests is not a status upgrade.
- Any *new* native or additional codec dependency is a fresh decision — this ADR authorises exactly
  one managed dependency (Concentus) for exactly one codec (Opus), not a general native-codec door.

## Sources

- Logs: `docs/archive/agent-log/2026-07-08-dev-codec-opus.md` (B.2/O1 — Opus/Concentus opt-in);
  decision inventory `docs/reference/decision-inventory.md` cluster C13.
- Code: `src/Core/Application/Media/Sessions/OpusPayloadCodec.cs`,
  `.../OpusDeviceCodec.cs`, `.../PayloadCodecKind.cs`,
  `.../AudioPayloadTranscoder.cs` (`:233`, `:327-328`),
  `src/Core/Infrastructure/Sdp/SdpUtilities.cs` (`:34-36`, `:475`),
  `src/Core/CalloraVoipSdk.Core.csproj` (`:13` Concentus 2.2.2).
- Marker/RFC/dep: RFC 7587 (Opus RTP, 48 kHz clock, §2.1 telephone-event, §4.1);
  Concentus 2.2.2 (managed C# Opus, MIT); founder decision "Concentus, kein natives Deployment".
