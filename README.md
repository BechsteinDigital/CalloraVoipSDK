# CalloraVoipSdk

[![CI](https://github.com/BechsteinDigital/callora-voip-sdk/actions/workflows/ci.yml/badge.svg)](https://github.com/BechsteinDigital/callora-voip-sdk/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-github%20pages-blue)](https://bechsteindigital.github.io/callora-voip-sdk/)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-support-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/bechsteindigital)

![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)
![.Net](https://img.shields.io/badge/.NET-5C2D91?style=for-the-badge&logo=.net&logoColor=white)

Commercial-grade .NET VoIP SDK for SIP signaling, RTP/SRTP media, WebRTC and PBX integrations.

CalloraVoipSdk is a .NET VoIP SDK (net8.0 / net9.0 / net10.0) for building softphones, PBX integrations, contact-center workflows and voice automation systems.  
It exposes a stable, developer-friendly API through `VoipClient` while keeping transport, media and device internals behind a clean facade — and opens up through a module registry for building products like AI voice agents on top.

📖 **Documentation:** [bechsteindigital.github.io/callora-voip-sdk](https://bechsteindigital.github.io/callora-voip-sdk/)
🧪 **Examples:** [`examples/`](examples) — runnable samples (BasicCalling, Dialer, Transfer, CustomAudio, VideoCalling, WebRtcPeer, WebRtcRecording, WebRtcDependencyInjection, and a browser video-call website `WebRtcVideoCall.Web`)
🛠️ **Maintainers:** [`MAINTAINING.md`](MAINTAINING.md) — architecture map, invariants, workflows; rules in [`ENGINEERING_RULES.md`](ENGINEERING_RULES.md)

> **Project status — 4.6.0.** The **SIP + RTP core** is the mature, production-oriented surface:
> registration, in/outbound call control, transfer, DTMF, SRTP (SDES) and measured RTCP quality
> metrics — with symmetric RTP (comedia) as the production-proven NAT path. It is exercised in CI
> by an **automated interop suite against a real Asterisk (PJSIP) container** — registration,
> in/outbound calls with live RTP, codec negotiation (PCMU/PCMA/G722), SRTP-SDES, DTMF (RFC 4733),
> hold, blind & attended transfer, session timers (RFC 4028), early media (RFC 3960) and TCP/TLS
> transport, plus a two-leg bridged call with **byte-exact bidirectional media** verified through
> the PBX — the matrix runs with zero skipped cases. The **WebRTC facade** and **DTLS-SRTP** are
> validated in CI against **real browsers** (Chromium and Firefox, headless via Playwright: audio
> and VP8 video, SDK as offerer *and* as answerer), and the **self-hostable STUN/TURN server** is
> exercised end-to-end against a real **coturn** relay. Deliberate limits in this line: no data
> channels (SCTP), TURN relay is **UDP-only**, simulcast is **send-side only**, and **full ICE**
> (RFC 8445/7675) is opt-in and not yet production-proven — validate it for your trunk before
> enabling it. Known gaps and interop defects are tracked openly in the
> [issue tracker](../../issues) — bug reports and interop feedback are especially welcome.

**Contents:** [Why](#why-calloravoipsdk) · [Progressive API](#progressive-api-simple-first-deeper-when-needed) · [Features](#current-feature-set) · [Install](#installation) · [Quickstart](#quickstart) · [Architecture](#architecture) · [Contributing](#contributing) · [Security](#security) · [License](#license)

## What's new in 4.6

- **WebRTC facade (transport-only)** — a signalling-neutral browser/peer surface in the
  `CalloraVoipSdk.WebRtc` namespace that mirrors the four-level design of `VoipClient`:
  `WebRtcClient.CreatePeer()` → `IPeerConnection` (ICE, DTLS-SRTP, BUNDLE, RTP/RTCP), a signalling
  happy path (`peer.ConnectAsync(signalling, role)`), the W3C track model (`TrackReceived` →
  `RemoteTrack`/`EncodedFrame`), a multi-peer manager (`client.Peers`), and L3 seams (`IMediaTap`,
  `IWebRtcClientModule`). The app owns signalling and the codec — the SDK moves bytes, it never
  encodes/decodes. Included: trickle ICE + early-bind (an ephemeral media port still yields a live
  m-line), mDNS `.local` candidate resolution (RFC 8828), NACK/PLI/FIR + RTX video repair
  (RFC 4585 / 5104 / 4588), transport-cc congestion feedback (RFC 8888), `getStats`, DTMF and RTCP
  quality on the BUNDLE path, and send-side simulcast (RFC 8853, offerer-confirmed). **Validated in
  CI against real browsers** — Chromium and Firefox, audio and VP8 video, SDK as offerer *and* as
  answerer. See the `WebRtc*` samples.
- **Self-hostable STUN & TURN server** — `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)`
  with TURN control over UDP/TCP/TLS, EVEN-PORT + RESERVATION-TOKEN (RFC 8656 §7), and a relay
  lifecycle that keeps allocations alive via permission-refresh and channel-rebind keepalives.
  Exercised end-to-end in CI against a real **coturn**.
- **AEAD-AES-GCM SRTP/SRTCP (RFC 7714)** — `AEAD_AES_128_GCM` and `AEAD_AES_256_GCM` end to end,
  offered preferred in DTLS-SRTP `use_srtp` with `AES_CM_128_HMAC_SHA1_80` as the interoperable
  fallback.
- **Early media (RFC 3960)** — receive-only media from a 180/183 SDP, a pre-answer call handle
  (`IPhoneLine.OutboundCallRinging`), `ICall.EarlyMediaSdp`, and DTMF while ringing (IVR / AI outbound).
- **SIP `MESSAGE` (RFC 3428)** and **SIP `PUBLISH` (RFC 3903)** with the full soft-state lifecycle,
  plus **REFER transfer progress subscriptions** (RFC 3515 / 6665) and
  **`ICall.TerminationReason`** — a protocol-neutral end cause that tells busy, unanswered,
  cancelled and rejected apart from a generic failure.
- **Local ICE restart** — `ICall.RestartIceAsync()` (RFC 8445 §9): new credentials on the existing
  socket, role preserved, so an app can both *detect* a dead media path and repair it.
- **Hardening across the stack** — a full source audit closed every interop- and stability-critical
  finding; CI grew **chaos/fault-injection** and **performance** gates alongside the existing
  interop, soak and browser suites. See [`CHANGELOG.md`](CHANGELOG.md) for the complete list.

**Known limits in this line:** no data channels (SCTP), TURN relay is UDP-only (no TCP/TLS relay),
simulcast is send-side only (receive-side RID demux is a later slice), Safari/WebKit is not yet
verified, and ICE-TCP candidates (RFC 6544) are a deliberate omission — real trunk calls run over
symmetric RTP (comedia), which needs no ICE or STUN.

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

## Progressive API: simple first, deeper when needed

CalloraVoipSdk does not force an early choice between a black-box wrapper and a raw
protocol stack. Start with managed workflows, then use deeper public contracts only
where the product needs them:

| Integration depth | Public surface | Typical use |
|---|---|---|
| Managed workflows | `ConnectAsync`, `DialAndWaitUntilConnectedAsync`, default audio, playback and recording | Softphones, dialers and standard call flows |
| Typed call control | `IPhoneLine`, `ICall`, transfers, DTMF, in-dialog SIP actions, negotiated media, quality/ICE state and outbound custom headers | Contact-center logic, routing, diagnostics and protocol-aware workflows |
| Media and extension seams | `IMediaReceiver`, `IMediaSender`, `MediaConnector`, custom `IAudioDevice` and telemetry implementations, `ModuleRegistry` | Voice bots, custom media routing, observability and separately shipped modules |

The levels can be mixed per call; using a convenience method does not close off the
lower-level contracts. SIP/RTP implementation classes and arbitrary wire mutation remain
internal, so this is controlled extensibility rather than an unstable exposure of the
entire transport stack. See [Progressive API](docs/portal/concepts/progressive-api.md)
for the decision guide and the media-threading constraints.

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
- Advanced call control: DTMF, blind & attended transfer with REFER progress tracking
  (RFC 3515 / 6665, via `TransferRequestedEventArgs.Subscription`)
- Early media (RFC 3960): pre-answer **receive-only** media from a 180/183 SDP, a pre-answer
  call handle (`IPhoneLine.OutboundCallRinging`), the early SDP on `ICall.EarlyMediaSdp`, and DTMF
  in the early dialog (`SendDtmfAsync` while ringing — IVR / AI outbound)
- In-dialog operations: `INFO`, `OPTIONS`, `SUBSCRIBE`, `NOTIFY`
- Messaging & presence: SIP `MESSAGE` (RFC 3428) send & receive
  (`VoipClient.SendMessageAsync` / `IncomingMessage` event) and SIP `PUBLISH` (RFC 3903, e.g.
  presence) with the full soft-state lifecycle — `PublishAsync` plus `RefreshPublicationAsync` /
  `ModifyPublicationAsync` / `RemovePublicationAsync` (`SIP-If-Match`)
- Media stack: RTP sessions, sender, receiver, `MediaConnector`, cross-connect
- Media encryption: SRTP via **SDES** (RFC 4568) or **DTLS-SRTP** (RFC 5763, opt-in via
  `VoipConfiguration.OfferDtlsSrtp`) as both caller and callee, encrypted/authenticated
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
- Self-hostable **STUN and TURN server** (RFC 5389 / RFC 5766 / RFC 8656) via
  `AddCalloraStunServer(...)` / `AddCalloraTurnServer(...)` — TURN control over UDP/TCP/TLS,
  EVEN-PORT + RESERVATION-TOKEN, permission/channel keepalives; the client side is verified in CI
  against a real coturn relay (UDP relay only)
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
- **WebRTC facade (`CalloraVoipSdk.WebRtc`)**: signalling-neutral browser/peer connections
  (`WebRtcClient.CreatePeer()`), an SDK-driven handshake (`peer.ConnectAsync(signalling, role)`), the
  W3C track model (`TrackReceived`/`RemoteTrack`/`EncodedFrame`), a multi-peer manager (`client.Peers`)
  and L3 media taps/modules — transport-only, bring your own codec (see [What's new in 4.6](#whats-new-in-46))
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

For SDK consumers, `VoipClient` is the central entry point, not the only useful
abstraction. Its typed sub-facades and public domain/media contracts form the supported
progressive API:

- `VoipClient` provides lifecycle ownership and managed workflows
- `IPhoneLine` and `ICall` provide typed call control and observable state
- public media, device, telemetry and module contracts are supported extension seams
- transport, parser and wire-level implementation classes remain internal details

This boundary keeps common integrations compact while still supporting advanced product
logic without depending on internal implementation types.

## Versioning

CalloraVoipSdk follows Semantic Versioning (`MAJOR.MINOR.PATCH`).

- Current public release line: `4.x` — latest release `4.6.0`
  (see [releases](https://github.com/BechsteinDigital/callora-voip-sdk/releases))
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
dotnet build CalloraVoipSdk.sln -c Release

# Architecture gates (CI runs these first)
dotnet test tests/CalloraVoipSdk.ArchitectureTests -c Release

# Standard test set (matches CI: excludes long soaks and Docker interop)
dotnet test CalloraVoipSdk.sln -c Release \
  --filter "Category!=SoakLong&Category!=Interop"

# Interop suite — real Asterisk (PJSIP) container via Testcontainers; needs a Docker daemon
# (tests self-skip when none is reachable)
dotnet test tests/CalloraVoipSdk.InteropTests -c Release --filter "Category=Interop"

# Linux workstation mode: runs Asterisk in Docker's host network without creating veth links.
# This avoids Chromium/Brave treating every test container as a host network change.
./scripts/test-interop-browser-safe.sh

# Long soak tests — media-quality drift + resource-leak/plateau guards over extended runs
dotnet test tests/CalloraVoipSdk.SoakTests -c Release --filter "Category=SoakLong"

# Manual machine-capacity envelope — real Asterisk echo, per-call/per-direction quality gate.
# Defaults ramp from 64 to 4096 calls and are intentionally not part of regular CI.
dotnet test tests/CalloraVoipSdk.InteropTests -c Release -f net10.0 \
  --filter "Category=Capacity"
```

> **Machine-bound capacity evidence (2026-07-28):** on an 8-core/16-thread
> Intel i7-11800H host using Server GC, a restricted Office power profile and a co-located
> Asterisk `Echo()` container, 1,408 full-duplex calls passed the strict per-call/per-direction
> gate in independent runs. A 1,792-call stage passed once but crossed the p99 timing gate in a
> repetition; at 1,920 calls every call remained connected with complete RTP/RTCP evidence and
> at least 99% media delivery, while 27 calls recorded a 41-ms inbound p99 against the 40-ms
> strict limit. The first thing to fail was scheduler timing — not RAM or CPU. These numbers
> describe that host/profile, not a global SDK maximum; measure your own envelope with the same
> gate. Sizing guidance:
> [Capacity and sizing](https://bechsteindigital.github.io/callora-voip-sdk/production/capacity.html).

The browser-safe interop runner is an explicit local Linux mode. It serializes Asterisk instances
because host networking uses the fixed SIP ports 5060/5061 and RTP port range, disables the
Testcontainers resource reaper only for that run, and removes only containers carrying its
dedicated cleanup label. The normal command and CI continue to use the isolated Docker bridge.

**Interop coverage.** The interop suite runs the full SIP/RTP flow against a real Asterisk
container in CI (currently **all cases green, none skipped**): registration (happy + failure),
in/outbound calls with live RTP, codec negotiation (PCMU/PCMA/G722), SRTP-SDES media, DTMF
(RFC 4733), hold/unhold, blind & attended transfer (REFER), session-timer negotiation (RFC 4028),
early media (RFC 3960, pre-answer receive + DTMF in the early dialog) and TCP/TLS transport. A
separate two-leg suite bridges two SDK legs through the PBX and verifies **bidirectional,
byte-exact media** (RTP counters both ways, local + remote RTCP quality, and byte-identical
PCMU payload A→B). The **two-leg scenario matrix** additionally runs against a real **FreeSWITCH**
container through the shared `IPbxFixture` abstraction — registration, bridged media (plain, SDES,
codec-mismatch transcoding), byte-exact content, RTCP, hold/unhold, attended transfer, DTMF and a
concurrent-call soak (trait `InteropFreeSwitch`, local-first — not in the PR gate). The Asterisk-only
scenarios are listed on the [FreeSWITCH page](docs/portal/interop/freeswitch.md).

**Browser + TURN coverage.** A dedicated browser-interop suite drives **Chromium and Firefox**
headless via Playwright through the full path — signalling → ICE → DTLS-SRTP → SRTP — with
bidirectional Opus and browser-decoded VP8, in **both** roles (SDK as offerer and as answerer). TURN
relay allocation and media are verified end-to-end against a real **coturn** server.

**Soak coverage.** The soak suite runs the media-quality matrix (PCMU/Opus × plain/SRTP) plus
resource plateau/leak guards over long runs (`SoakShort` on PRs, `SoakLong` nightly), asserting
no jitter-buffer overflow, correct loss accounting, and no monotone resource drift.

> The full test matrix and the L0–L4 test model are documented in
> [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`MAINTAINING.md`](MAINTAINING.md).
> The capacity profile, quality gates, report fields and interpretation limits are documented in
> [`docs/maintainers/capacity-quality-benchmark.md`](docs/maintainers/capacity-quality-benchmark.md).

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
[Video calls guide](https://bechsteindigital.github.io/callora-voip-sdk/guides/video-calls.html).

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

- Full ICE (RFC 8445 / RFC 7675) is **implemented and opt-in** — role + tie-breaker, check-list
  FSM, `USE-CANDIDATE` nomination, inbound/triggered checks, restart detection **and local restart
  initiation** (`ICall.RestartIceAsync`, RFC 8445 §9 — new credentials on the existing socket, role
  preserved), and consent freshness with media cease. Final state and selected pair are observable via
  `ICall.IceSnapshot`; post-establishment changes (incl. consent loss → `Disconnected`) via
  `ICall.IceConnectionStateChanged`. Remaining gaps toward production ([#62](../../issues/62)): live
  interop/production validation of the ICE path, and ICE-TCP candidates (RFC 6544) — a **deliberate
  post-GA limitation**, since real trunk calls run over symmetric RTP (comedia), which needs no ICE or STUN.
- Commercial plugin line-up (private feed, licensed): Callora.Realtime, WebSocket
  streaming, Privacy/Risk/Intelligence — in development
- CI/CD hardening: soak, Asterisk interop, chaos/fault-injection, and performance gates are all in place
  (media-quality matrix, resource-leak guards, full SIP/RTP flow against a real container with zero skipped
  cases, a two-leg byte-exact bidirectional media test, a per-PR chaos gate that injects transport loss,
  malformed/adversarial packets, signaling outage, and resource churn under fault and asserts graceful
  degradation + recovery + no leak, and a per-PR performance gate that holds the SRTP per-packet crypto hot
  path above a catastrophic-regression throughput floor); the machine-specific capacity envelope runs nightly
- Broader interop validation against more PBXs, trunks and browsers. Automated in CI today:
  Asterisk (PJSIP), Chromium, Firefox and coturn. FreeSWITCH is automated but local-first;
  FRITZ!Box is manually verified; Safari/WebKit and the commercial trunks are configuration
  guidance so far — see the [interop matrix](docs/portal/interop/matrix.md)

## License

Licensed under the Apache License, Version 2.0. See [`LICENSE`](LICENSE).

## Contributing

Contributions, issues and interop reports are welcome — this is our first open-source
release, so real-world feedback (which PBX/trunk/browser you tested against, what broke) is
especially valuable.

- **Start here:** [`CONTRIBUTING.md`](CONTRIBUTING.md) — setup, test levels, and the bar for a PR.
- **Report a bug / request a feature:** open an [issue](../../issues) (templates provided). For
  larger changes, open an issue first so architecture and API impact can be discussed.
- **Good first issues:** browse the
  [`good first issue`](../../issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) label.
- **Maintainer docs:** [`MAINTAINING.md`](MAINTAINING.md), [`ENGINEERING_RULES.md`](ENGINEERING_RULES.md)
  and [`docs/maintainers/`](docs/maintainers/).
- **Code of Conduct:** [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).

## Security

Please report security vulnerabilities **privately** — see [`SECURITY.md`](SECURITY.md) — and
not as a public issue. This SDK handles SIP digest authentication and SRTP/DTLS keying, so
responsible disclosure matters.

## Support

The SDK core is open and free (Apache-2.0). If it helps you build something, you can support
ongoing development on **[Ko-fi](https://ko-fi.com/bechsteindigital)** ☕. For commercial
plugins or early access, contact [info@bechstein.digital](mailto:info@bechstein.digital).
