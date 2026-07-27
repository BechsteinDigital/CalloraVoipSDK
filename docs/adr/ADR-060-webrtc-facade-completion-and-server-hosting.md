# ADR-060: WebRTC Facade Completion — Fluent ICE Config, Send-Side Simulcast, and the TURN/STUN Server-Hosting Facade

Status: Accepted
Date: 2026-07-19

## Context

ADR-009 set the WebRTC browser-peer roadmap and ADR-012 fixed the public **peer/client** facade
(`WebRtcClient : IWebRtcClient`, the four-level architecture, `IPeerConnection`, the `TrackReceived`
model, `WebRtcOptions`/`WebRtcConfiguration`, `AddCalloraWebRtc` → `CalloraWebRtcBuilder`). Both
ADRs were written *before* the code and, as the build order landed, three tracing decisions were
made that neither ADR records — and in two cases the shipped surface diverges from the ADR sketch.
This ADR closes that gap without re-deciding what ADR-009/012 already own.

Verified against the code (2026-07-27):

1. **Fluent ICE-server configuration diverged from ADR-012 §5.** ADR-012 named the builder methods
   `WithIceServers`/`WithDtlsCertificate`/`WithSignaling`/`WithModule`. The shipped
   `CalloraWebRtcBuilder` (`src/Client/WebRtc/CalloraWebRtcBuilder.cs`) instead offers
   `WithStunServer(host, port?)`, `WithTurnServer(host, user, pass, port?, transport?)`,
   `WithIceServers(params IceServerConfiguration[])`, `WithVideo(params codecs)`,
   `WithDtlsCertificate`, and `WithLoggerFactory`. `WithSignaling`/`WithModule` were **not** built;
   ICE-server config (STUN/TURN accumulating into `WebRtcOptions.IceServers`) was, and is the primary
   config surface a browser-facing app needs. The accumulation semantics (each call appends via
   `PostConfigure` so it composes with the caller's own `Action<WebRtcOptions>`) are a real API
   contract that no ADR states.

2. **Send-side simulcast is on the public peer.** `IPeerConnection`
   (`src/Client/WebRtc/IPeerConnection.cs`) ships `SendVideoFrameAsync(string rid, …)` (RFC 8853)
   alongside the single-stream overload, backed by `WebRtcConfiguration.SimulcastLayers`
   (mapped 1:1 from `WebRtcOptions` in `WebRtcOptionsMapping`). ADR-009 §3 explicitly deferred
   simulcast ("Screensharing / mehrere Video-Tracks / Simulcast folgen danach"); ADR-012's peer
   sketch does not mention it. The send-side layer contract — app encodes each rid at its own
   resolution/bitrate and calls once per layer per frame; recv-side demux is still out — is
   undocumented. (The peer also grew `CreateOffer`/`SetRemoteDescriptionAsync`-returns-answer,
   `AddIceCandidateAsync`, `GatherCandidatesAsync`, `SendDtmfAsync`, `VideoKeyFrameRequested`, and
   `AttachMediaTap`, i.e. the ADR-012 §4/§6 seams landed with different method names than the ADR
   sketch's `CreateAnswerAsync`/`AddRemoteIceCandidateAsync`.)

3. **A TURN/STUN server-hosting facade exists and is in neither ADR.** `src/Client/Hosting/`
   (namespaces `CalloraVoipSdk.Hosting` + `CalloraVoipSdk.DependencyInjection`) exposes
   `AddCalloraTurnServer`/`AddCalloraStunServer` → `CalloraTurnServerBuilder`/`CalloraStunServerBuilder`,
   the runtime hosts `ITurnServerHost`/`IStunServerHost` (both `IAsyncDisposable`, bind-on-construct,
   `Start()`), and the options/config pair `TurnServerHostOptions`/`TurnServerHostConfiguration` (and
   the STUN equivalents) with `ToConfiguration`. This turns the previously test-only Core
   `TurnServer`/`StunServer` into a supported product surface. ADR-009/012 only ever describe the SDK
   as a WebRTC *client/peer*; running a TURN/STUN *server* is a distinct product decision with its own
   guardrails (it is a separately deployable host, not part of `WebRtcClient` or `VoipClient`).

Not in scope here (owned elsewhere, do not duplicate):
- The TURN-relay **media datapath** (relay as ICE candidate, control stack, whole-socket transition):
  cluster **C16** (`_draft-C16-01/02/03`).
- The **send-side ICE agent** in the BUNDLE transport (`IceMediaConsentSession`,
  `IceNominationDriver`, `IceMediaAttachment`, checked nomination): cluster **C11**
  (`_draft-C11-01/02/03`); C16 builds on it.

## Decision

### 1. The WebRTC config surface is ICE-server-first and accumulating

`CalloraWebRtcBuilder` is the L3 override seam for the WebRTC facade (mirroring `CalloraBuilder` for
SIP). Its ICE configuration is the primary surface: `WithStunServer` / `WithTurnServer` add typed
`IceServerConfiguration` entries; `WithIceServers` adds fully-specified entries; **all three
accumulate** (append, never replace) so they compose with an app's own `AddCalloraWebRtc(configure)`.
`WithVideo`, `WithDtlsCertificate`, `WithLoggerFactory` round out the L3 knobs. The ADR-012 §5
`WithSignaling`/`WithModule` names are superseded: signaling is wired per-connection through
`ConnectPeerAsync`/`IWebRtcSignaling`, and modules through the shared `IModuleRegistry`, not through
builder methods.

### 2. Send-side simulcast is a first-class, transport-only peer capability

`IPeerConnection.SendVideoFrameAsync(string rid, …)` (RFC 8853) is supported for the **send** side:
the app supplies `WebRtcConfiguration.SimulcastLayers`, encodes each layer itself, and pushes one
already-encoded frame per rid per frame. Consistent with ADR-012's transport-only guardrail, the SDK
packetises and routes per rid but does not encode. **Recv-side simulcast demux remains out of scope**
(a later slice) — this ADR only records that the *send* direction shipped.

### 3. The TURN/STUN server host is a separate, deployable facade — not the peer

`AddCalloraTurnServer`/`AddCalloraStunServer` register a standalone host (`ITurnServerHost` /
`IStunServerHost`) that a deployment runs to *provide* relay/reflexive service to peers. It is a peer
of `AddCalloraVoip`/`AddCalloraWebRtc`, not a sub-feature of either client. It reuses the four-level
option/config/builder shape (mutable `*HostOptions` for DI, immutable `*HostConfiguration` for direct
construction, `ToConfiguration` projection, a fluent `Calloira*ServerBuilder` for bind endpoint /
realm / credentials / transport / TLS / public relay address). The host wraps the Core
`TurnServer`/`StunServer`, promoting them from test-only to product surface, without moving server
code into the client facades.

## Consequences

Positive: the shipped WebRTC public surface (fluent ICE config, send-side simulcast) and the
TURN/STUN hosting facade are now recorded and RFC-anchored; the ADR set matches the code, so a
reader is not misled by ADR-012 §5's superseded method names or by the absence of any server-hosting
decision. A deployment can self-host relay/reflexive infrastructure through a supported API.

Tradeoffs: three public surfaces (WebRTC builder, peer simulcast overload, hosting facade) are now
under public-API stability. The hosting facade widens the product's remit from pure client to
client-plus-infrastructure, and inherits the "no server code in the SIP/WebRTC client cores"
constraint. Send-side-only simulcast is an asymmetric capability until recv-side demux lands.

## Guardrails

- The TURN/STUN server host stays a **separate** facade under `CalloraVoipSdk.Hosting`; no server
  code leaks into `WebRtcClient`/`VoipClient` or the SIP/WebRTC transport cores.
- Simulcast stays **transport-only** (app owns the encoder; SDK packetises per rid); no "simulcast
  ready" claim covers recv-side demux until that slice ships.
- Builder ICE methods **accumulate**; a method must never silently replace previously configured ICE
  servers.
- ADR-009/012 remain the source of truth for the peer facade, four-level architecture, and
  `TrackReceived` model; this ADR only records the deltas above. TURN-relay datapath → C16;
  BUNDLE send-side ICE → C11 (not restated here).
- No "WebRTC-ready/production" claim without browser-interop validation (inherited from ADR-009/012).

## Sources

- `docs/adr/ADR-009-webrtc-browser-peer-roadmap.md` (§3 simulcast deferral, §4 facade, §6 TURN)
- `docs/adr/ADR-012-webrtc-public-facade.md` (§5 builder — superseded method names; §4 track model)
- `src/Client/WebRtc/CalloraWebRtcBuilder.cs` (`WithStunServer`/`WithTurnServer`/`WithIceServers`)
- `src/Client/WebRtc/IPeerConnection.cs` (`SendVideoFrameAsync(rid, …)`, `AttachMediaTap`)
- `src/Client/WebRtc/WebRtcOptionsMapping.cs` (`SimulcastLayers` projection)
- `src/Client/Hosting/` (`ITurnServerHost`, `IStunServerHost`, `AddCalloraTurnServer`,
  `AddCalloraStunServer`, `CalloraTurnServerBuilder`, `*HostOptions`/`*HostConfiguration`)
- Out of scope: `_draft-C16-01/02/03` (TURN-relay datapath), `_draft-C11-01/02/03` (send-side ICE)
