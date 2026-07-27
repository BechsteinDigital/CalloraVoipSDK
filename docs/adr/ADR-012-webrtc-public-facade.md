# ADR-012: WebRTC Public Facade (`WebRtcClient`)

Status: Accepted
Date: 2026-07-18

## Context

The WebRTC transport core is feature-complete internally (ADR-009/010): ICE-connection-state
events, BUNDLE B1–B5, `a=msid` track identity (both directions), and the C6 Send-vs-Dispose
hardening are all built and tested — but the whole stack sits behind `internal
WebRtcPeerConnection` and is unusable by any app. Exposing it is the gateway to the browser-MVP
milestone (and to browser-interop validation).

The exposure must mirror the SDK's established public-API architecture, not invent a new one. That
architecture is **four levels** (verified against the `VoipClient` SIP facade):

1. **L1 — Facade** (`VoipClient`): happy path + product-oriented convenience.
2. **L2 — Managers** (`Calls`/`Lines`/`Media`/`Sessions`/`Policies`/`Quality`/`Telemetry`):
   advanced application control, all mockable interfaces.
3. **L3 — Ports, Modules, Media-Taps, swappable implementations**: own integrations and
   extensions (DI-injectable ports via `ResolveService<T>`, `IModuleRegistry`, media frame taps,
   `SdkConfiguration.Services`).
4. **L4 — internal SIP/RTP/SRTP/Infrastructure**: deliberately no stable public API (`internal`).

WebRTC is signaling-neutral (ADR-009 §4): the app owns the signaling channel (WebSocket/HTTP/
Callora); the SDK provides a peer + SDP-in/SDP-out + ICE. That is a different developer model than
SIP (register/dial), so it needs its own facade — not a bolt-on to `VoipClient`.

## Decision

### 1. `WebRtcClient` mirrors the four-level architecture

A public `WebRtcClient : IWebRtcClient` (namespace `CalloraVoipSdk.WebRtc`, in `src/Client`) that
offers exactly the same four levels as `VoipClient`. L4 (peer/BUNDLE/DTLS/ICE/SRTP) stays
`internal`.

### 2. Two facades, composed — not one flat client, not two islands

- One flat client mixing `Dial()` and `CreatePeer()` is rejected: it merges two mental models
  (Account/Line/Call vs Peer/Offer/Answer/Track) into one surface — objectively worse DX,
  independent of any guardrail.
- Two disconnected clients are rejected for the CPaaS case: bridging Browser ⇄ SDK ⇄ SIP would
  force the app to wire media between two clients by hand.
- **Chosen:** separate capability facades under one optional composing entry — the same pattern
  `VoipClient` already uses to compose its managers, one level up (protocol level). `VoipClient`
  (SIP) and `WebRtcClient` (WebRTC) each work standalone; a future `CalloraClient` composes both
  and adds `BridgeAsync(peer, sipCall)`. Composition at the *facade* level keeps WebRTC out of the
  SIP *core* (ADR-009 guardrail satisfied).

Naming: **`VoipClient` is NOT renamed** (correct for the SIP/telephony stack; renaming is
binary-breaking + cross-repo for no DX gain). WebRTC facade = **`WebRtcClient`** (clear parallel,
no prejudice). A future unified entry, if ever wanted, is **`CalloraClient`** (brand-first,
scope-agnostic) — preferred over `RtcClient` (jargon) / `CommunicationClient` (clunky) — and would
*compose* the two facades, not rename them.

### 3. Signaling-adapter happy path + raw peer for the expert

- **L1 happy path:** the app plugs in a thin signaling channel (`IWebRtcSignaling`: send/receive
  one message) and calls `ConnectPeerAsync(signaling, …)`; the SDK drives the whole
  offer/answer/ICE/DTLS/BUNDLE handshake. As easy as SIP's register+dial.
- **Expert path:** `CreatePeer()` returns the raw `IPeerConnection` (SDP-in/SDP-out); the app owns
  the SDP transport.

### 4. Track model for inbound media (`TrackReceived`)

Inbound media is surfaced per **track** (W3C model), not as flat audio/video events and not as one
combined A/V event:

```csharp
event EventHandler<RemoteTrack> TrackReceived;

sealed class RemoteTrack { TrackKind Kind; string? StreamId; string? TrackId; event …<EncodedFrame> FrameReceived; }
readonly struct EncodedFrame(ReadOnlyMemory<byte> Payload, uint? RtpTimestamp, bool IsKeyFrame, long? PresentationTimeUsec);
```
> As-shipped (step 4): `RtpTimestamp` is `uint?` (null for audio — the inbound audio path surfaces only
> the payload; present for video), and `StreamId`/`TrackId` are nullable (the remote may advertise no
> `a=msid`; RFC 8830 `-` → null). Nullable is more honest than a fake `0`/empty. `PresentationTimeUsec`
> stays `null` until the RTCP-SR mapping lands.

Rationale: A and V arrive as separate RTP streams; a muxer wants per-track samples with
presentation timestamps and interleaves itself. Sync drift comes from *missing timing*, not from
separate delivery. The track model serves both driving use cases from one shape:

- **Meet + recording:** subscribe to all tracks of a `StreamId` (one participant), mux by
  `PresentationTimeUsec` → lip-synced, no post-hoc re-sync.
- **AI voice into video:** subscribe to the audio track alone (independent STT→LLM→TTS, inject TTS
  on the outbound audio track) without touching video.

`RemoteTrack.StreamId` **is the `a=msid` stream id** built in the msid slice — the payoff of doing
msid first.

### 5. Options/config/DI — 1:1 like `VoipClient`

Mutable `WebRtcOptions` (`{get;set;}`, DI/options) and immutable `WebRtcConfiguration`
(`{get;init;}`, direct construction); `WebRtcOptions.ToConfiguration(loggerFactory)` is a pure 1:1
projection. `services.AddCalloraWebRtc(Action<WebRtcOptions>)` → `CalloraWebRtcBuilder`
(`WithIceServers`/`WithDtlsCertificate`/`WithSignaling`/`WithModule`). `ResolveService<T>`
(DI-or-default) throughout the constructor.

### 6. L2 managers are shared, not duplicated

`WebRtcClient` reuses the Core managers (`IMediaManager`, `IQualityManager`, `ISessionManager`,
`ITelemetryManager`, `IDeviceManager`, `IModuleRegistry`) and adds only `IPeerConnectionManager
Peers`. Shared media types are what make a future bridge feasible.

### Build order (one commit each)

1. `IPeerConnection` + adapter surfacing the internal peer (behaviour-neutral) + tests.
2. `WebRtcClient`/`IWebRtcClient` + `IPeerConnectionManager` (peer registry) + `CreatePeer` + tests.
3. `WebRtcOptions`/`WebRtcConfiguration` + `AddCalloraWebRtc`/`CalloraWebRtcBuilder` +
   `ResolveService` seams + tests.
4. Track model: `TrackReceived`/`RemoteTrack`/`EncodedFrame` wired from the peer's inbound path + tests.
5. Signaling adapter: `IWebRtcSignaling` + `ConnectPeerAsync` (SDK drives the handshake) + tests.
6. L3 opening: media taps on the peer + `IModuleRegistry` binding + a sample.

## Consequences

Positive: the built WebRTC stack becomes usable; the facade unlocks browser-interop validation; a
dev who knows `VoipClient` gets `WebRtcClient` for free (same DNA); the design is bridge-ready
without prejudicing `VoipClient`/`WebRtcClient` names.

Tradeoffs: a public API surface is expensive to change — hence this ADR before code. Some sub-parts
are seams whose implementation lands later (below).

## Deferred / follow-up slices

- **Trickle-ICE:** `LocalIceCandidateDiscovered` / `AddRemoteIceCandidateAsync` + a
  `WebRtcSignal.Candidate` kind. v1 candidates ride in the SDP (the offer carries the host
  candidate).
- **Codec-pipeline ports** (`IVideoSource`/`IVideoSink`/`IVideoEncoder`/`IVideoDecoder`, ADR-009
  §5): defined as an L3 seam now; implementation with `CalloraVoipSdk.Video.FFmpeg`. Transport-only
  until then (the app supplies encoded frames via L1).
- **RTCP-SR RTP↔NTP mapping** → fills `EncodedFrame.PresentationTimeUsec` for cross-track
  lip-sync; `null` until then (per-track `RtpTimestamp` still delivered).
- **`CalloraClient` + `BridgeAsync`** (Browser ⇄ SIP): additive, composes the two facades.
- **SCTP DataChannels, TURN/TCP/TLS:** after the audio/video MVP (ADR-009 §6/§7).

## Guardrails

- No "WebRTC-ready/production" claims without browser-interop validation (Chrome/Firefox).
- No WebRTC code in the SIP signalling core; composition is allowed only at the facade level.
- Public API stability applies to L1–L3; L4 (`Infrastructure`) stays `internal` and unstable.
- Transport-only until the FFmpeg codec package: the app owns the codec; the SDK packetises.
