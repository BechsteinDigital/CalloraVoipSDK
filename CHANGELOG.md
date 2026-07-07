# Changelog

All notable changes to this project are documented in this file.

The format is based on Keep a Changelog and this repository follows Semantic Versioning (SemVer).

## [Unreleased]

## [3.0.0] - 2026-07-07

### Added
- Generic module registry as the SDK extension point: `IVoipClientModule`, `ModuleRegistry`, `IVoipClient.Modules`. Modules from separate packages register via DI (`IVoipClientModule` services before `AddCallora`) or programmatically via `client.Modules.Register(...)`; typed resolution via `Get<T>`/`TryGet<T>`. The `OnAttached` hook hands modules the owning client; modules only become resolvable after the hook completed.
- Per-call media tap pinned as a public, tested contract: parallel `IMediaReceiver` fan-out, detach/dispose isolation, `IMediaSender` injection and format discovery via `ICall.MediaParameters`. XML docs now state the blocking contract (`FrameReceived` runs synchronously on the media path; consumers must buffer).

### Changed
- `VoipClient` construction now disposes already created runtime resources when a module throws during `OnAttached`, then rethrows the original error.

### Removed
- **Breaking:** `ModuleOperationResult` removed (unreferenced since 2.0.0).

## [2.0.0] - 2026-07-07

### Removed
- **Breaking:** Unimplemented module facades removed from the public SDK surface: `IConferencingModule`, `IConferenceSession`, `IRealtimeModule`, `ICallRealtimeBridge`, `IAudioFrameStreamTransport`, `IWebSocketModule`, `IWebSocketAudioTransportModule` and related option/event types.
- **Breaking:** `IVoipClient`/`VoipClient` properties `ConferenceManager`, `RealtimeManager` and `WebSocketManager` removed. `ModuleManager` no longer exposes `Conferencing`, `Realtime`, `WebSocket` or their availability flags.
- **Breaking:** `SessionManager.ActiveConferences` removed.
- These features previously threw `ModuleFeatureUnavailableException` on every call; they return as separate plugin packages built on the public media API.

### Added
- `net9.0` and `net10.0` target frameworks in addition to `net8.0`.

### Fixed
- SRTP RFC 3711 IV derivation and auth handling.
- PRACK wait race during reliable provisional handling.
- CANCEL transaction branch handling; BYE is now sent when CANCEL loses the INVITE race.

## [1.0.2] - 2026-04-17

### Added (previously listed under Unreleased)
- `SdkOptionsValidator` with startup validation for DI-based configuration.

### Changed (previously listed under Unreleased)
- `AddCallora(...)` now validates options on startup (`ValidateOnStart`).
- `CalloraHostedService` now coordinates lifecycle through `VoipClient` runtime hooks.
- DI path no longer uses `SdkConfiguration.Services` escape-hatch.
- `RegisterAndWaitAsync(...)` is now marked obsolete in favor of `ConnectAsync(...)`.

### Deprecated
- `SdkConfiguration.Services` is deprecated and will be removed after `v1.0`.

## [0.9.0] - 2026-04-14

### Added
- DDD-based source layout under `src/Core`, `src/Client`, `src/Modules`, `src/Audio`, `src/Hosting`, `src/Licensing` and `src/Abstractions`.
- Convenience-first `VoipClient` managers and module facades (`ConferenceManager`, `PlaybackManager`, `RecordingManager`, `ModuleManager`, `SessionManager`, `DeviceManager`, `QualityManager`, `PolicyManager`, `TelemetryManager`).
- Full DI entrypoint (`AddCallora`, `IVoipClient`, builder overrides, hosted lifecycle wrapper).
- Updated docs and docfx configuration for the new structure.

### Fixed
- Namespace and project-reference mismatches after modular split.
- Architecture tests and documentation paths aligned to `src/Core`.
