# CalloraVoipSdk

[![CI](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CalloraVoipSdk.Core)](https://www.nuget.org/packages/CalloraVoipSdk.Core)
![Coverage](https://img.shields.io/badge/coverage-cobertura%20artifact-blue)

![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)
![.Net](https://img.shields.io/badge/.NET-5C2D91?style=for-the-badge&logo=.net&logoColor=white)

Commercial-grade .NET VoIP SDK for SIP signaling, RTP media, PBX integrations and voice automation.

CalloraVoipSdk is a .NET 8 VoIP SDK for building softphones, PBX integrations, contact-center workflows and voice automation systems.  
It exposes a stable, developer-friendly API through `VoipClient` while keeping transport, media and device internals behind a clean facade.

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
- Linux audio devices via `CalloraVoipSdk.Audio.Linux`
- Windows audio devices via `CalloraVoipSdk.Audio.Windows`
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

- Current public release line: `1.0.x`
- Public API changes are guarded by snapshot tests in `tests/CalloraVoipSdk.Core.Tests/PublicApi.approved.txt`
- Deprecations are introduced through `[Obsolete(...)]` before removal
- Consumer-relevant changes are documented in [`CHANGELOG.md`](CHANGELOG.md)

## Requirements

- .NET SDK 8.0+ (will be updated soon to .NET SDK 10.0+)
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

using var client = new VoipClient(new SdkConfiguration
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

## Production guidance

- Dispose and unregister `VoipClient`, `IPhoneLine` and `ICall` cleanly
- Execute call actions only in valid states such as `Connected`, `Ringing` or `OnHold`
- Keep event handlers short and non-blocking under load
- Choose audio providers explicitly via platform-specific packages
- Treat infrastructure details as non-public integration surface

## Roadmap to 1.0.1

The current `1.0.0` line is already usable, but `1.0.1` is the first stable public release target.

Typical focus areas on the road to `1.0.0`:

- public API hardening
- more end-to-end examples
- additional RFC coverage
- stronger interoperability validation
- documentation and package ergonomics

## License

Licensed under the Apache License, Version 2.0. See [`LICENSE`](LICENSE).

## Contributing

Contributions, issues and discussions are welcome.

If you plan to contribute larger changes, open an issue first so architecture and API impact can be discussed before implementation.
