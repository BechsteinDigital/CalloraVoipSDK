# CalloraVoipSdk

[![CI](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/BechsteinDigital/CalloraVoipSdk/actions/workflows/ci.yml)
![Coverage](https://img.shields.io/badge/coverage-cobertura%20artifact-blue)

CalloraVoipSdk ist eine .NET-8-VoIP-SDK für PBX-, Contact-Center- und Voice-Automation-Szenarien.  
Die API ist DDD-orientiert aufgebaut und bietet eine stabile Facade über `VoipClient`.

## Status

Aktuell verfügbar (Stand im Repository):

- SIP-Grundfunktionen: Register, Invite/Dial, Accept, Hangup, Hold/Unhold
- Erweiterte Call-Steuerung: DTMF, Blind/Attended Transfer
- In-Dialog-Operationen: `INFO`, `OPTIONS`, `SUBSCRIBE`, `NOTIFY`
- Medienpfad: RTP-Sessions, Sender/Receiver, `MediaConnector` (inkl. Cross-Connect)
- Audio-Geräte für Linux und Windows als separate Projekte
- Runtime Device Controls: Device-Hot-Switch, Input/Output Mute, Input/Output Volume, Format-Update
- Umfangreiche RFC-orientierte Unit- und Compliance-Tests

## Architektur (DDD)

- `src/Core/Domain`: Entitäten, Value Objects, Zustände, Domain-Events
- `src/Core/Application`: Use-Cases und Orchestrierung (Calls, Lines, Media)
- `src/Core/Infrastructure`: SIP/RTP/SDP/Audio-Adaptionen
- `src/Client`: öffentliche Facade und DX-Manager (`VoipClient`, Convenience APIs, DI)

## API-Sichtbarkeit

- Für SDK-Nutzer ist `VoipClient` der zentrale Einstiegspunkt.
- `Infrastructure`-Typen gelten als interne Implementierungsdetails und sind kein stabiler Integrationsvertrag.
- `Application`-Typen werden nur dort sichtbar gehalten, wo sie für typische SDK-Nutzung zwingend nötig sind.

## Versionierung

- Wir folgen Semantic Versioning (`MAJOR.MINOR.PATCH`).
- Aktuelle Pre-Release-Linie: `0.9.x`; `1.0.0` ist das erste stabile Public Release.
- Öffentliche API-Änderungen werden über Snapshot-Tests abgesichert (`tests/CalloraVoipSdk.Core.Tests/PublicApi.approved.txt`).
- Deprecations laufen über `[Obsolete(...)]` vor einer Entfernung.
- Consumer-relevante Änderungen stehen in [`CHANGELOG.md`](CHANGELOG.md).
- Details: [`docs/SEMVER_POLICY.md`](docs/SEMVER_POLICY.md).

## Voraussetzungen

- .NET SDK 8.0+
- SIP-Account/PBX-Zugangsdaten
- Für echte Audio-I/O auf Linux: `CalloraVoipSdk.Audio.Linux` (PortAudio-basiert)
- Für echte Audio-I/O auf Windows: `CalloraVoipSdk.Audio.Windows` (NAudio-basiert)

## Build, Test

```bash
dotnet restore CalloraVoipSdk.sln
dotnet build CalloraVoipSdk.sln
dotnet test CalloraVoipSdk.sln
```

## Einbindung ins eigene Projekt

Per NuGet (Feed-Konfiguration vorausgesetzt):

```bash
dotnet add package CalloraVoipSdk.Core
dotnet add package CalloraVoipSdk.Audio.Windows   # Windows
dotnet add package CalloraVoipSdk.Audio.Linux     # Linux
```

Alternativ per `ProjectReference` (lokale Entwicklung):

```xml
<ItemGroup>
  <ProjectReference Include="..\voip\src\Client\CalloraVoipSdk.Client.csproj" />
  <ProjectReference Include="..\voip\src\Core\CalloraVoipSdk.Core.csproj" />
  <!-- Optional je nach Plattform -->
  <ProjectReference Include="..\voip\src\Audio\Linux\CalloraVoipSdk.Audio.Linux.csproj" />
  <!-- oder -->
  <ProjectReference Include="..\voip\src\Audio\Windows\CalloraVoipSdk.Audio.Windows.csproj" />
</ItemGroup>
```

## Quickstart: Happy Path (Convenience)

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
    throw new InvalidOperationException($"Connect fehlgeschlagen: {connectResult.Status}");

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
    throw new InvalidOperationException($"Dial fehlgeschlagen: {dialResult.Status}");

var call = dialResult.Call;
await client.AttachDefaultAudioAsync(call);

// Runtime audio controls (works for configured SDK audio device).
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

await call.SendDtmfAsync(new DtmfTone('5'));
await call.HoldAsync();
await call.UnholdAsync();
await call.HangupAsync();
```

## Erweiterter Event-Flow (für Power-User, unverändert)

```csharp
var line = client.Lines.Register(account);

line.StateChanged += (_, e) =>
    Console.WriteLine($"Line: {e.OldState} -> {e.NewState}");

var call = await line.DialAsync("sip:1002@pbx.example.com");
call.StateChanged += (_, e) =>
    Console.WriteLine($"Call {e.Call.CallId}: {e.OldState} -> {e.NewState}");
```

## Beispiel 2: Inbound Call annehmen, ablehnen oder umleiten

```csharp
using var subscription = client.OnIncomingCall(async call =>
{
    Console.WriteLine($"Inbound von: {call.RemoteParty}");

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

## Beispiel 3: Medienpfad manuell steuern (Advanced)

```csharp
using CalloraVoipSdk.Audio.Linux; // Auf Windows: CalloraVoipSdk.Audio.Windows + WindowsAudioDevice
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

## Beispiel 4: Zwei Calls direkt gegeneinander bridgen

```csharp
// Voraussetzung: callA und callB sind verbunden.
using var aRx = client.Media.CreateReceiver();
using var aTx = client.Media.CreateSender();
using var bRx = client.Media.CreateReceiver();
using var bTx = client.Media.CreateSender();

aRx.AttachToCall(callA);
aTx.AttachToCall(callA);
bRx.AttachToCall(callB);
bTx.AttachToCall(callB);

using var bridge = client.Media.CreateConnector().CrossConnect(aRx, aTx, bRx, bTx);

// bridge.Dispose() trennt die Medienkopplung wieder.
```

## Wichtige Hinweise für Produktion

- `VoipClient`, `IPhoneLine`, `ICall` sauber über Lifecycle steuern und am Ende disposen/unregistern
- Call-Aktionen zustandsabhängig ausführen (`Connected`, `Ringing`, `OnHold`)
- Bei hoher Last Event-Handler kurz halten und nicht-blockierend implementieren
- Audio-Provider als separate Plattform-Module bewusst auswählen
