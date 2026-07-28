# Architecture Overview

CalloraVoipSdk follows Domain-Driven Design (DDD) with strict layer separation.

## Layer Responsibilities

```
src/Core/
  Domain/          Entities, Value Objects, States, Domain Events
                   (Call, PhoneLine, CallState, LineState)
  Application/     Use-cases and orchestration
                   (CallManager, MediaManager)
                   Port interfaces: ISdpNegotiator, IAudioDevice, ICallIceAgent
  Infrastructure/  Protocol adapters — SIP, SDP, RTP, RTCP, SRTP, DTLS,
                   STUN, TURN, WebRTC, Audio
                   (not used directly by SDK consumers)

src/Client/
  Application/Facades    Public SDK entrypoint (`VoipClient`)
  Application/Managers   Developer-facing convenience/runtime managers
  WebRtc/                Public WebRTC entrypoint (`WebRtcClient`, `IPeerConnection`)
  Hosting/               Self-hostable STUN / TURN server hosts
  Infrastructure/DI      Host integration and dependency wiring
```

## Two facades

The SDK ships two public facades that mirror the same shape — mutable `*Options` → immutable
`*Configuration` → client → module registry as the plugin seam:

| Facade | Entrypoint | DI | Scope |
|--------|-----------|----|-------|
| SIP | `VoipClient` | `AddCalloraVoip(...)` | Registration, calls, SIP media |
| WebRTC | `WebRtcClient` | `AddCalloraWebRtc(...)` | Peer connections, BUNDLE media ([guide](../guides/webrtc.md)) |

They compose in one chain: `services.AddCalloraVoip(sip => …).AddWebRtc(rtc => …)`.

## Two media session families

| | Single stream (SIP calls) | BUNDLE (WebRTC) |
|---|---|---|
| Socket | one per m-line | one 5-tuple for everything (RFC 8843), MID/RID demux |
| Keying | SDES (RFC 4568) **or** DTLS-SRTP | DTLS-SRTP only |
| Jitter/PLC | adaptive jitter buffer + playout loop + concealment | video reorder buffer (no jitter buffer) |
| Repair / feedback | NACK/RTX, PLI/FIR, transport-cc per stream | the same, with **one transport-wide** transport-cc plane per bundle |

## Dependency Rule

Dependencies flow **inward only**: Client facade → Core Application → Core Domain. Infrastructure implements Application ports.

## VoipClient

All runtime operations go through `VoipClient`:

| Property | Responsibility |
|----------|---------------|
| `client.Lines` | Register / unregister SIP lines |
| `client.Calls` | Query active calls |
| `client.Media` | Create senders, receivers, connectors |
| `client.Modules` | Register and resolve feature modules (`IVoipClientModule`, `Get<T>`/`TryGet<T>`) — the plugin extension point |

## Events

All state changes are delivered as events on the relevant domain object:

| Object | Event | When |
|--------|-------|------|
| `IPhoneLine` | `StateChanged` | Registration state change |
| `ICall` | `StateChanged` | Call state change |
| `VoipClient` | `IncomingCall` | Inbound INVITE received |
