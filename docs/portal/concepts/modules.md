# Modules

The **module registry** (`client.Modules`, `ModuleRegistry`) is the SDK's extension
point. It lets separately-shipped capabilities plug into a running client without the
core taking a dependency on them.

## Resolving modules

```csharp
// Typed resolution:
var recording = client.Modules.Get<IRecordingModule>();
if (client.Modules.TryGet<IPlaybackModule>(out var playback))
{
    // …
}
```

The built-in recording and playback modules are also surfaced directly as
`client.RecordingManager` / `client.PlaybackManager` for convenience.

## Why a registry

- The core (`CalloraVoipSdk.Core`) stays free of feature-specific dependencies.
- Capabilities are discovered and resolved by contract (`Get<T>` / `TryGet<T>`), not by
  hard references.
- It is the seam the [commercial modules](../commercial/index.md) build on — Realtime,
  WebSocket, Privacy, Risk and Intelligence attach here and on the
  [media-tap contract](../guides/media-tap.md).

## Building on it

A module implements the SDK's module contract and is registered against the client. It
can then use the public media-tap surface (`CreateReceiver`/`CreateSender`) to observe
or inject call audio. This is exactly how the commercial plugins integrate, and the same
door is open to your own in-house modules.
