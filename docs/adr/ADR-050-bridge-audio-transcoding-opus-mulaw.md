# ADR-050: Bridge Audio Transcoding — Wire Codec ↔ µ-law Tap

Status: Accepted
Date: 2026-07-08

## Context

Once Opus can be negotiated on the SIP wire (see the C13 Opus-integration ADR), a second problem
appears at the **bridge tap** — the point where SDK audio is handed to a fixed-codec consumer. The
canonical consumer is the OpenAI realtime bridge, which speaks **G.711 µ-law only**. If the SIP
peer negotiates Opus (or A-law) while the tap expects µ-law, feeding the raw negotiated payload
straight through delivers garbage to the consumer.

The O1 (Opus-integration) log is explicit about this gap: it forbade pointing an agent's
`PreferredAudioCodecs` at Opus until a transcoding bridge existed, because the agent path is
µ-law passthrough to OpenAI and an Opus wire without transcoding would be "Garbage Richtung
OpenAI". O2 built that bridge.

### Verified current state (graphify-grounded)

- **`BridgeAudioTranscoder`** (`src/Core/Application/Media/Sessions/BridgeAudioTranscoder.cs`)
  transcodes between the negotiated **wire** codec and the fixed **tap** codec (µ-law only today).
- **No resampler.** The intermediate is always PCM16 at the **8 kHz** telephony rate. G.711
  variants are already 8 kHz; Opus decodes/encodes **directly at 8 kHz** because the transcoder
  constructs its `OpusPayloadCodec` with `TapSampleRate = 8000` (`:21`, `:32`) — Concentus
  resamples internally, so no separate resampler is needed.
- **Supported wire codecs**: `CreateForPcmuTap` returns `null` for PCMU (identity passthrough — no
  transcode), builds a transcoder for **Opus** and **PCMA** (`:53-62`), and returns `null` with a
  **logged warning** for anything not 8 kHz-native, e.g. G.722 (`:63-68`) — the raw payload is
  delivered and the caller is told bridge audio will be incorrect (explicit, not silent).
- **Directional transcode** (`:72-98`): inbound `WireToTap` — Opus payload → `_opus.Decode` →
  `PcmG711Codec.EncodeMuLaw`; A-law → decode → encode µ-law. Outbound `TapToWire` — µ-law →
  `PcmG711Codec.DecodeMuLaw` → `_opus.Encode` (Opus) / re-encode A-law. This is genuine
  encode/decode on both edges, reusing the same Concentus-backed `OpusPayloadCodec`.
- **Fixed payload types**: tap PT is always PCMU static PT 0 (`TapPayloadType => 0`); the wire PT
  is carried through (`WirePayloadType`, e.g. Opus PT 107).
- **Fail-safe on decode error** (per O2 log): an inbound decode failure yields µ-law silence rather
  than tearing down the media loop.
- **Wiring**: `RtpCallMediaSession` builds the transcoder when the bridge tap codec is PCMU and the
  wire kind is transcodable and ≠ PCMU; `SdkConfiguration.BridgeAudioFormat`
  (`src/Client/Domain/Configuration/BridgeAudioFormat.cs`, public enum Passthrough|Pcmu) maps
  through VoipClient → factory → session (per O2 log).

## Decision

Add a **narrow, resampler-free transcoding tap** between the negotiated wire codec and a fixed
bridge codec:

1. **Transcode only within the 8 kHz telephony rate.** Intermediate is always PCM16@8k; decode Opus
   directly at 8 kHz (Concentus internal resampling) so no external resampler enters the media hot
   path.
2. **Support exactly the 8 kHz-native wire codecs** for a µ-law tap: Opus and PCMA transcode; PCMU
   is identity (null transcoder); non-8k-native codecs (G.722) are **explicitly refused with a
   logged warning**, not silently mishandled.
3. **Fail safe, not silent**: an inbound decode error emits µ-law silence and keeps the loop alive.
4. **Config-gated**: `SdkConfiguration.BridgeAudioFormat` (Passthrough default, or Pcmu) selects
   whether the tap transcodes at all; Passthrough leaves everything byte-identical.

### Crux

The crux is a **scope discipline** decision, not a codec decision: rather than build a general
audio resampler (the "correct" but heavy answer that would also let G.722 bridge), the bridge is
pinned to the **8 kHz telephony rate that both edges already share**, and refuses anything that
would need a resampler. That keeps the OpenAI-bridge use case working end-to-end with real
Opus↔µ-law transcoding while quarantining the resampler as future work — a conscious "narrow and
honest" choice over "broad and heavy".

## Consequences

Positive:
- Unblocks the real founder use case: an agent may negotiate Opus on the SIP wire while the OpenAI
  bridge stays µ-law — previously (O1) this was forbidden as it produced garbage.
- No resampler in the media hot path; Opus and G.711 both live at 8 kHz, so the tap stays cheap.
- Reuses the same Concentus-backed `OpusPayloadCodec` (the C13 Opus decision), just at 8 kHz —
  no second codec path.
- Passthrough default and PCMU-wire+PCMU-tap remain byte-identical (regression-tested), so the tap
  cannot regress existing bridges.

Tradeoffs / honest divergence:
- **Narrowband only.** The bridge decodes Opus at 8 kHz (narrowband); Opus wideband quality never
  reaches OpenAI — but the bridge is µ-law@8k anyway, so this is inherent, not a regression.
- **G.722 (and any non-8k-native codec) is not bridged.** It is refused with a warning and the raw
  payload is delivered — bridge audio will be incorrect for that combination. Correct support needs
  the deferred resampler.
- **No loss concealment via the codec.** Opus PLC/FEC is not used; on loss the playout path
  transcodes whatever it received. DTX/FEC tuning is still Concentus defaults.
- **Not production-proven.** O2 permits the real test (agent `PreferredAudioCodecs=["opus"]` +
  `BridgeAudioFormat=Pcmu`), but audio quality is founder acceptance — no "production-ready" claim
  from green tests alone.

## Guardrails

- The tap transcodes **only** at 8 kHz; introducing any non-8k wire codec (e.g. G.722) into the
  bridge requires the deferred resampler and is a fresh decision, not a quiet extension.
- Unsupported wire codecs must be **refused with a log**, never silently passed as if transcoded
  (the `default` branch warns and returns null).
- Inbound decode errors must fail safe (µ-law silence), never tear down the media loop
  (K6 "kein stummer catch" applies to the surrounding path).
- Passthrough (`BridgeAudioFormat` default) and PCMU-wire+PCMU-tap must stay byte-identical — the
  transcoder is only built when the wire kind is transcodable and ≠ PCMU.
- One transcoder instance per call leg; it is not internally thread-safe and relies on the media
  session's serialized inbound/outbound paths.
- No "Opus bridge production-ready" claim without the real sipgate test and founder quality sign-off.

## Sources

- Logs: `docs/archive/agent-log/2026-07-08-dev-codec-opus-bridge.md` (B.2/O2 — bridge transcoding);
  `docs/archive/agent-log/2026-07-08-dev-codec-opus.md` (O1 — the gap O2 closes);
  decision inventory `docs/reference/decision-inventory.md` cluster C13.
- Code: `src/Core/Application/Media/Sessions/BridgeAudioTranscoder.cs`
  (`:21`, `:32`, `:46-70`, `:72-98`), `.../OpusPayloadCodec.cs` (8 kHz ctor path),
  `.../PcmG711Codec.cs` (µ-law/A-law encode/decode), `.../PayloadCodecKind.cs`,
  `src/Client/Domain/Configuration/BridgeAudioFormat.cs` (public Passthrough|Pcmu enum).
- Marker/RFC/dep: RFC 7587 (Opus, decoded here at 8 kHz narrowband); G.711 µ-law/A-law
  (8 kHz telephony); Concentus 2.2.2 (internal resampling to 8 kHz).
