# CalloraVoipSdk

[![CI](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
![Coverage](https://img.shields.io/badge/coverage-cobertura%20artifact-blue)
[![Docs](https://img.shields.io/badge/docs-github%20pages-blue)](https://bechsteindigital.github.io/CalloraVoipSDK/)

![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)
![.Net](https://img.shields.io/badge/.NET-5C2D91?style=for-the-badge&logo=.net&logoColor=white)

Commercial-grade .NET VoIP SDK for SIP signaling, RTP media, PBX integrations and voice automation.

CalloraVoipSdk is a .NET VoIP SDK (net8.0 / net9.0 / net10.0) for building softphones, PBX integrations, contact-center workflows and voice automation systems.  
It exposes a stable, developer-friendly API through `VoipClient` while keeping transport, media and device internals behind a clean facade — and opens up through a module registry for building products like AI voice agents on top.

📖 **Documentation:** [bechsteindigital.github.io/CalloraVoipSDK](https://bechsteindigital.github.io/CalloraVoipSDK/)
🧪 **Examples:** [`examples/`](examples) — runnable samples (BasicCalling, Dialer, Transfer, CustomAudio, VideoCalling, WebRtcPeer, WebRtcRecording, WebRtcDependencyInjection, and a browser video-call website `WebRtcVideoCall.Web`)

## What's new in 4.6 (preview)

- **WebRTC facade (preview, transport-only)** — a signalling-neutral browser/peer surface in the
  `CalloraVoipSdk.WebRtc` namespace that mirrors the four-level design of `VoipClient`:
  `WebRtcClient.CreatePeer()` → `IPeerConnection` (ICE, DTLS-SRTP, BUNDLE, RTP/RTCP), a signalling
  happy path (`peer.ConnectAsync(signalling, role)`), the W3C track model (`TrackReceived` →
  `RemoteTrack`/`EncodedFrame`), a multi-peer manager (`client.Peers`), and L3 seams (`IMediaTap`,
  `IWebRtcClientModule`). The app owns signalling and the codec — the SDK moves bytes, it never
  encodes/decodes. See the `WebRtc*` samples. **Preview:** not yet browser-validated (Chrome/Firefox),
  API may change; no data channels / TURN / simulcast yet.

> **Breaking change in 4.6** — the SIP-facade configuration types were renamed so each facade owns a
> facade-scoped name (parallel to `WebRtcConfiguration`/`WebRtcOptions`/`AddCalloraWebRtc`):
> `SdkConfiguration` → `VoipConfiguration`, `SdkOptions` → `VoipOptions`, `AddCallora(...)` →
> `AddCalloraVoip(...)`. There are no compatibility aliases — rename these three symbols at your call
> sites. `VoipClient` and all other public types are unchanged; behaviour is identical. See
> [`CHANGELOG.md`](CHANGELOG.md).

## Why CalloraVoipSdk

CalloraVoipSdk is built for developers who need more than a black-box telephony wrapper.

- Stable public API centered around `VoipClient`
- Full SIP call control for outbound and inbound scenarios
- RTP media pipeline with sender, receiver and cross-connect support
- Runtime audio device control for Linux and Windows
- DDD-oriented architecture with clear boundaries
- Extensive RFC-oriented unit and compliance tests

## Typical use cases

- Softphones and operator desktops
- PBX and SIP integrations
- Contact-center and queue workflows
- Voice bots and automation systems
- Media bridging and custom audio routing
- Real-time call control in backend services

## Current feature set

Available in the repository today:

- SIP basics: register, invite/dial, accept, hangup, hold/unhold
- Advanced call control: DTMF, blind transfer, attended transfer
- In-dialog operations: `INFO`, `OPTIONS`, `SUBSCRIBE`, `NOTIFY`
- Media stack: RTP sessions, sender, receiver, `MediaConnector`, cross-connect
- Media encryption: SRTP via SDES (RFC 4568) as both caller and callee, encrypted/authenticated
  RTCP via SRTCP (RFC 3711 §3.4), a configurable per-call policy
  (`VoipConfiguration.SrtpPolicy`: Disabled / Optional / Required), re-keying on re-INVITE, and the
  negotiated suite name / SRTCP status readable on `ICall.MediaParameters`
- Transport selection: choose the default outbound SIP transport
  (`VoipConfiguration.DefaultTransport`: UDP / TCP / TLS / WS / WSS; default UDP)
- Custom outbound headers (`DialOptions.CustomHeaders`, injection-guarded) and read-only remote
  identity on inbound calls (`ICall.RemoteAssertedIdentity` / `ICall.Diversion`)
- Per-call observability: ICE state + selected candidate pair (`ICall.IceSnapshot`) and raw
  RFC 3550 RTP counters (`ICall.RtpStatistics`)
- NAT/trunk controls: public signaling contact (`SipAccount.PublicSipHost`) and an opt-in public
  media address for CGNAT / static 1:1 NAT (`SipAccount.PublicMediaHost`)
- Per-call media tap: attach frame receivers/senders to any call for bots, bridging
  and streaming scenarios (`client.Media.CreateReceiver()/CreateSender()`)
- Encoded video (transport-only): send/receive encoded frames
  (`client.Media.CreateVideoReceiver()/CreateVideoSender()`), a ready-to-use recommended
  outbound bitrate + `NetworkQuality` from transport-cc feedback, inbound keyframe flags and
  RTCP PLI/FIR keyframe-request feedback, plus a default-video convenience
  (`client.AttachDefaultVideoAsync(call)` with an application-supplied `IVideoDevice` codec).
  The SDK never encodes/decodes — bring your own VP8/H.264 codec
- Module registry (`client.Modules`) as the extension point for separately shipped
  feature modules
- **WebRTC facade (preview, `CalloraVoipSdk.WebRtc`)**: signalling-neutral browser/peer connections
  (`WebRtcClient.CreatePeer()`), an SDK-driven handshake (`peer.ConnectAsync(signalling, role)`), the
  W3C track model (`TrackReceived`/`RemoteTrack`/`EncodedFrame`), a multi-peer manager (`client.Peers`)
  and L3 media taps/modules — transport-only, bring your own codec (see [What's new in 4.6](#whats-new-in-46-preview))
- Configurable audio codec preference (`VoipConfiguration.PreferredAudioCodecs`)
- RTCP quality metrics with measured values: local/remote jitter, packet loss and
  round-trip time from SR/RR (LSR/DLSR); RFC 3611 XR-tolerant compound decoding
- Linux audio devices via `CalloraVoipSdk.Audio.Linux`
- Windows audio devices via `CalloraVoipSdk.Audio.Windows`
- Device codec support: PCMU, PCMA, G.722 and native Opus (RFC 7587, 48 kHz)
- Runtime device controls:
  - device hot-switch
  - input/output mute
  - input/output volume
  - format updates
- RFC-oriented unit and compliance test coverage

## Package layout

- `CalloraVoipSdk`  
  Public entry point and developer-facing facade

- `CalloraVoipSdk.Core`  
  Core call, line, media and protocol abstractions

- `CalloraVoipSdk.Audio.Windows`  
  Windows audio integration based on NAudio

- `CalloraVoipSdk.Audio.Linux`  
  Linux audio integration based on PortAudio

## Architecture

The solution follows a DDD-oriented structure:

- `src/Core/Domain`  
  Entities, value objects, states and domain events

- `src/Core/Application`  
  Use cases and orchestration for calls, lines and media

- `src/Core/Infrastructure`  
  SIP, RTP, SDP and audio-specific implementations

- `src/Client`  
  Public facade, convenience APIs and dependency injection wiring

## Public API boundary

For SDK consumers, `VoipClient` is the central entry point.

- `VoipClient` is the supported integration surface
- `Infrastructure` types are internal implementation details
- `Application` types are only exposed where necessary for practical SDK usage

This keeps the external API compact and stable while allowing internal evolution.

## Versioning

CalloraVoipSdk follows Semantic Versioning (`MAJOR.MINOR.PATCH`).

- Current public release line: `4.x` (see [releases](https://github.com/BechsteinDigital/CalloraVoipSDK/releases))
- Public API removals only happen in MAJOR releases; deprecations are introduced
  through `[Obsolete(...)]` before removal
- Consumer-relevant changes are documented in [`CHANGELOG.md`](CHANGELOG.md)

## Requirements

- .NET SDK 8.0+ (packages target `net8.0`, `net9.0` and `net10.0`)
- SIP account or PBX credentials
- For real audio I/O on Linux: `CalloraVoipSdk.Audio.Linux`
- For real audio I/O on Windows: `CalloraVoipSdk.Audio.Windows`

## Installation

### NuGet

```bash
dotnet add package CalloraVoipSdk
dotnet add package CalloraVoipSdk.Audio.Windows   # Windows
dotnet add package CalloraVoipSdk.Audio.Linux     # Linux
```

### Local development via `ProjectReference`

```xml
<ItemGroup>
  <ProjectReference Include="..\voip\src\Client\CalloraVoipSdk.Client.csproj" />
  <ProjectReference Include="..\voip\src\Core\CalloraVoipSdk.Core.csproj" />
  <ProjectReference Include="..\voip\src\Audio\Linux\CalloraVoipSdk.Audio.Linux.csproj" />
  <ProjectReference Include="..\voip\src\Audio\Windows\CalloraVoipSdk.Audio.Windows.csproj" />
</ItemGroup>
```

## Build and test

```bash
dotnet restore CalloraVoipSdk.sln
dotnet build CalloraVoipSdk.sln
dotnet test CalloraVoipSdk.sln
```

## Quickstart

### 1. Connect and place a call

```csharp
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk;

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

using var client = new VoipClient(new VoipConfiguration
{
    LoggerFactory = loggerFactory,
    UserAgent = "MySoftphone/1.0",
    MaxConcurrentCallsPerLine = 4
});

var connectResult = await client.ConnectAsync(
    new SipAccount
    {
        Username = "1001",
        Password = "secret",
        SipServer = "pbx.example.com",
        DisplayName = "Agent 1001",
        Transport = SipTransport.Tls
    },
    new ConnectOptions
    {
        Timeout = TimeSpan.FromSeconds(15),
        FailFastOnRegistrationFailed = true
    });

if (!connectResult.IsSuccess || connectResult.Line is null)
    throw new InvalidOperationException($"Connect failed: {connectResult.Status}");

var line = connectResult.Line;

var dialResult = await client.DialAndWaitUntilConnectedAsync(
    line,
    "sip:1002@pbx.example.com",
    new DialWaitOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        HangupOnTimeout = true,
        HangupOnCancellation = true
    });

if (!dialResult.IsSuccess || dialResult.Call is null)
    throw new InvalidOperationException($"Dial failed: {dialResult.Status}");

var call = dialResult.Call;

await client.AttachDefaultAudioAsync(call);

await call.SendDtmfAsync(new DtmfTone('5'));
await call.HoldAsync();
await call.UnholdAsync();
await call.HangupAsync();
```

### 2. Runtime audio device control

```csharp
var inDevices = client.GetAvailableInputAudioDevices();
var outDevices = client.GetAvailableOutputAudioDevices();

if (inDevices.Count > 1)
    client.SwitchAudioInputDevice(inDevices[1].Id);

if (outDevices.Count > 1)
    client.SwitchAudioOutputDevice(outDevices[1].Id);

client.SetAudioInputVolume(0.8f);
client.SetAudioOutputVolume(1.1f);
client.SetAudioInputMuted(false);
client.SetAudioOutputMuted(false);

client.UpdateAudioFormat(new AudioDeviceFormat
{
    SampleRate = 16000,
    BitsPerSample = 16,
    Channels = 1
});
```

### 3. Handle inbound calls

```csharp
using var subscription = client.OnIncomingCall(async call =>
{
    Console.WriteLine($"Inbound from: {call.RemoteParty}");

    if (IsInLunchBreak())
    {
        await call.RejectAsync(486, "Busy Here");
        return;
    }

    if (ShouldForwardToQueue(call.RemoteParty))
    {
        var result = await call.RedirectAsync(["sip:queue@pbx.example.com"], statusCode: 302);
        Console.WriteLine($"Redirect: {result.Status}");
        return;
    }

    await call.AcceptAsync();
    await client.AttachDefaultAudioAsync(call);
});

static bool IsInLunchBreak() => false;
static bool ShouldForwardToQueue(string remoteParty) => false;
```

### 4. Advanced event-driven flow

```csharp
var line = client.Lines.Register(account);

line.StateChanged += (_, e) =>
    Console.WriteLine($"Line: {e.OldState} -> {e.NewState}");

var call = await line.DialAsync("sip:1002@pbx.example.com");

call.StateChanged += (_, e) =>
    Console.WriteLine($"Call {e.Call.CallId}: {e.OldState} -> {e.NewState}");
```

### 5. Manual media control

```csharp
using CalloraVoipSdk.Audio.Linux;
using CalloraVoipSdk.Core.Application.Ports.Audio;
using CalloraVoipSdk.Core.Domain.Calls;

using var audioDevice = new LinuxAudioDevice();
using var receiver = client.Media.CreateReceiver();
using var sender = client.Media.CreateSender();

call.StateChanged += (_, e) =>
{
    if (e.NewState == CallState.Connected)
    {
        receiver.AttachToCall(call);
        sender.AttachToCall(call);

        var audioParameters = call.MediaParameters is { } mp
            ? AudioConnectionParameters.From(mp)
            : AudioConnectionParameters.Default;

        audioDevice.Connect(receiver, sender, audioParameters);

        if (audioDevice is IAudioDeviceRuntimeControl runtime)
        {
            runtime.SetInputMuted(false);
            runtime.SetOutputMuted(false);
            runtime.SetInputVolume(0.9f);
            runtime.SetOutputVolume(1.0f);
        }
    }

    if (e.NewState == CallState.Terminated)
    {
        audioDevice.Disconnect();
        receiver.Detach();
        sender.Detach();
    }
};
```

### 6. Bridge two active calls

```csharp
using var aRx = client.Media.CreateReceiver();
using var aTx = client.Media.CreateSender();
using var bRx = client.Media.CreateReceiver();
using var bTx = client.Media.CreateSender();

aRx.AttachToCall(callA);
aTx.AttachToCall(callA);
bRx.AttachToCall(callB);
bTx.AttachToCall(callB);

using var bridge = client.Media.CreateConnector().CrossConnect(aRx, aTx, bRx, bTx);
```

### 7. Pin the audio codec

```csharp
using var client = new VoipClient(new VoipConfiguration
{
    UserAgent = "MyVoiceBot/1.0",
    // Order = preference. Offers/answers only include the listed codecs (plus DTMF
    // telephone-event), and RTP sessions pick their primary codec accordingly.
    // Useful for passthrough scenarios, e.g. G.711 µ-law towards a realtime AI API.
    PreferredAudioCodecs = ["PCMU"]
});
```

### 8. Video call (bring your own codec)

Video is **transport-only** — the SDK moves encoded frames but never encodes or decodes.
Attach a receiver/sender to a call and drive your own VP8/H.264 codec. The SDK hands you a
ready-to-use recommended bitrate and surfaces peer keyframe requests.

```csharp
using CalloraVoipSdk.Core.Application.Media;

using var videoIn = client.Media.CreateVideoReceiver();
using var videoOut = client.Media.CreateVideoSender();
videoIn.AttachToCall(call);
videoOut.AttachToCall(call);

// Inbound: decode encoded frames yourself (handler runs on the media path — never block).
videoIn.FrameReceived += (_, e) => myDecoder.Decode(e.Frame.Payload);

// The payoff: let the SDK size your encoder to the network.
videoOut.RecommendedBitrateChanged += (_, e) => encoder.SetBitrate(e.RecommendedBitrateBps);
videoOut.KeyFrameRequested += (_, _) => encoder.ForceKeyFrame();

// Outbound: send already-encoded frames.
await videoOut.SendAsync(new VideoFrame(encodedBytes, PayloadType: 96, RtpTimestamp: ts, IsKeyFrame: false));
```

Prefer the "audio-simple" path? Package your codec behind an `IVideoDevice`, register it in
DI, and call `await client.AttachDefaultVideoAsync(call)`. Full walkthrough:
[Video calls guide](https://bechsteindigital.github.io/CalloraVoipSDK/guides/video-calls.html).

## Extending the SDK — module registry

`client.Modules` is the extension point for feature modules that ship as separate
packages. A module implements `IVoipClientModule`, gets attached to the client and is
then resolvable by any interface it implements:

```csharp
// Register (or inject via DI as IVoipClientModule before AddCalloraVoip):
client.Modules.Register(new MyRecordingModule());

// Resolve anywhere:
var recording = client.Modules.Get<IMyRecordingFeature>();      // throws if unavailable
if (client.Modules.TryGet<IMyRecordingFeature>(out var feature)) // or probe
    feature.Start();
```

Modules build on the public per-call media tap. Its contract in two sentences:
`IMediaReceiver.FrameReceived` fires **synchronously on the media path** — handlers must
buffer and return immediately, never block. Negotiated format details (payload type,
clock rate, samples per packet) are available via `ICall.MediaParameters`.

### Commercial plugins (private, paid — in development)

On top of this extension point we are building a set of commercial plugins, distributed
through a private feed (not on nuget.org):

- **Callora.Realtime** — bridge call audio to realtime AI APIs (e.g. OpenAI Realtime)
  with pacing, backpressure and barge-in support; powers AI voice agents
- **Callora.WebSocket** — raw call-audio streaming over WebSocket
- **Callora.Privacy / Callora.Risk / Callora.Intelligence** — redaction & consent,
  spam/scam screening, AMD/transcription/sentiment

The SDK core stays open and free; plugins are licensed separately. Contact
[info@bechstein.digital](mailto:info@bechstein.digital) for early access.

## Production guidance

- Dispose and unregister `VoipClient`, `IPhoneLine` and `ICall` cleanly
- Execute call actions only in valid states such as `Connected`, `Ringing` or `OnHold`
- Keep event handlers short and non-blocking under load
- Choose audio providers explicitly via platform-specific packages
- Treat infrastructure details as non-public integration surface
- ICE (RFC 8445 / RFC 7675) is opt-in (`IceConfiguration.Enabled` defaults to `false`) and, while
  largely implemented, remains unproven in production — validate it for your trunk before enabling
  it. The production-proven NAT path is symmetric RTP (comedia), which needs no ICE or STUN.

## Roadmap

- Full ICE (RFC 8445 / RFC 7675): the agent now implements role derivation + tie-breaker,
  the check-list state machine, regular nomination with `USE-CANDIDATE`, inbound connectivity
  and triggered checks, ICE restart detection, and consent freshness with media cease — but it
  remains **opt-in and unproven in production** (no live trunk validation yet). The final ICE
  state and selected candidate pair are now observable via `ICall.IceSnapshot`. Remaining gaps:
  TCP candidates, and surfacing consent loss to the application for a restart/terminate decision.
  Real trunk calls run over symmetric RTP (comedia), which needs no ICE or STUN.
- Commercial plugin line-up (private feed, licensed): Callora.Realtime, WebSocket
  streaming, Privacy/Risk/Intelligence — in development
- CI/CD hardening: soak, interop and chaos gates
- More end-to-end examples and interoperability validation

## License

Licensed under the Apache License, Version 2.0. See [`LICENSE`](LICENSE).

## Contributing

Contributions, issues and discussions are welcome.

If you plan to contribute larger changes, open an issue first so architecture and API impact can be discussed before implementation.
