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
  Infrastructure/  Protocol adapters — SIP, RTP, SRTP, SDP, STUN, Audio
                   (not used directly by SDK consumers)

src/Client/
  Application/Facades    Public SDK entrypoint (`VoipClient`)
  Application/Managers   Developer-facing convenience/runtime managers
  Infrastructure/DI      Host integration and dependency wiring
```

## Dependency Rule

Dependencies flow **inward only**: Client facade → Core Application → Core Domain. Infrastructure implements Application ports.

## VoipClient

All runtime operations go through `VoipClient`:

| Property | Responsibility |
|----------|---------------|
| `client.Lines` | Register / unregister SIP lines |
| `client.Calls` | Query active calls |
| `client.Media` | Create senders, receivers, connectors |

## Events

All state changes are delivered as events on the relevant domain object:

| Object | Event | When |
|--------|-------|------|
| `IPhoneLine` | `StateChanged` | Registration state change |
| `ICall` | `StateChanged` | Call state change |
| `VoipClient` | `IncomingCall` | Inbound INVITE received |
