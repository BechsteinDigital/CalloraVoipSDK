# VoipClient

`VoipClient` is the single public entry point. It owns the SIP stack, the media
pipeline and the audio devices, and exposes everything else through typed sub-facades.
It implements `IDisposable` — construct one per application (or per tenant) and dispose
it on shutdown.

```csharp
using var client = new VoipClient(new VoipConfiguration { /* … */ });
```

## Sub-facades

| Property | Type | Responsibility |
|----------|------|----------------|
| `Lines` | `PhoneLineManager` | Registration and the registered [lines](lines.md) |
| `Calls` | `CallManager` | The active [calls](calls.md) |
| `Media` | `MediaManager` | Receivers, senders, connectors, [recording/playback](media.md) |
| `Modules` | `ModuleRegistry` | The [module](modules.md) extension point (`Get<T>`/`TryGet<T>`) |
| `DeviceManager` | `DeviceManager` | Audio device enumeration and runtime control |
| `QualityManager` | `QualityManager` | RTCP quality snapshots |
| `PolicyManager` | `PolicyManager` | Runtime policy (e.g. inbound acceptance) |
| `SessionManager` | `SessionManager` | Lifecycle of underlying sessions |
| `TelemetryManager` | `TelemetryManager` | Diagnostic sinks/traces |
| `RecordingManager` / `PlaybackManager` | module facades | Convenience recording/playback |

## Convenience methods

The client also carries the high-level verbs most apps need directly:

- `ConnectAsync(SipAccount)` — register and return the line
- `DialAndWaitUntilConnectedAsync(line, uri)` — dial and await answer
- `AttachDefaultAudioAsync(call)` / `DetachDefaultAudioAsync(call)`
- `OnIncomingCall(handler)` — subscribe to inbound calls with an `IDisposable`
- Audio device control: `GetAvailableInputAudioDevices()`,
  `SwitchAudioInputDevice(id)`, `SetAudioInputVolume(v)`, `SetAudioInputMuted(b)`,
  `UpdateAudioFormat(format)` (and the matching output variants) — see
  [Audio devices](../guides/audio-devices.md)

## Configuration

`VoipConfiguration` is an immutable (`init`-only) options record:

| Property | Default | Meaning |
|----------|---------|---------|
| `UserAgent` | `"CalloraVoipSdk/1.0"` | SIP `User-Agent` header |
| `LoggerFactory` | `null` | `ILoggerFactory` for diagnostics |
| `DefaultTransport` | `Udp` | Default outbound SIP transport: UDP / TCP / TLS / WS / WSS |
| `Tls` | `null` | Client-wide TLS settings for the TLS/WSS transports. Per-line client certificates and server-trust modes: [SIP over TLS (mutual TLS)](../guides/sip-tls-mtls.md) |
| `SrtpPolicy` | `Optional` | `Disabled` / `Optional` / `Required` — see [SRTP/SRTCP](../guides/srtp-srtcp.md) |
| `OfferDtlsSrtp` | `false` | Offer DTLS-SRTP keying (RFC 5763) instead of SDES |
| `RequireSecureSignalingForSdes` | `false` | Refuse SDES keying over insecure signalling (UDP/TCP/WS) |
| `DtlsCertificate` | `null` | Certificate for the DTLS-SRTP handshake; self-signed when unset |
| `PreferredAudioCodecs` | `null` | Ordered codec preference for offers/answers |
| `BridgeAudioFormat` | `Passthrough` | Wire format when bridging two calls (`Passthrough` or `Pcmu`) |
| `EnableVideo` | `false` | Negotiate a video m-line ([transport-only](../guides/video-calls.md)) |
| `PreferredVideoCodecs` | `null` | Ordered video codec preference |
| `AudioDevice` | `SilenceAudioDevice` | Default audio device when none is attached |
| `EnableAutomaticAudioDeviceSelection` | `true` | Load the platform audio package by reflection |
| `MaxConcurrentCallsPerLine` | `10` | Guard against runaway call fan-out |
| `InboundMediaTimeout` | `15 s` | Drop inbound calls with no media |
| `HangupHeldCallOnMediaSilence` | `false` | Also apply the media timeout to held calls |
| `Ice` | `IceConfiguration` | Full ICE (RFC 8445/7675) — opt-in, off by default, unproven in production trunks |
| `Services` | `null` | `IServiceProvider` used to resolve ports (DI composition) |

## Lifecycle

`Dispose()` unregisters lines and makes a best-effort BYE for still-active calls.
Details and graceful-shutdown guidance: [Lifecycle & dispose](../production/lifecycle-dispose.md).
