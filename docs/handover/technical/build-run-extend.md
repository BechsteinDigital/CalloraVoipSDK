# Build, Test, Ausführen & Erweitern

> **Teil des technischen Due-Diligence-Pakets.** Stand: 2026-07-27.
>
> Diese Seite zeigt einem übernehmenden Team, wie es CalloraVoipSdk **baut, testet, ausführt und
> erweitert** — von den Voraussetzungen über die Solution-Struktur bis zu den öffentlichen
> Erweiterungspunkten (Modul-Contract, Media-Tap, DI, WebRTC- und Server-Hosting-Fassaden).
> Alle Befehle, Projektnamen und Seams sind gegen den Quellbaum (`src/`, `examples/`, `tests/`)
> und die Projektdateien verifiziert; wo Prosa und Code auseinanderlaufen, gilt der Code
> (Prinzip „Doku ≤ Nachweis").

---

## 1. Voraussetzungen

- **.NET SDK.** Alle produktiven Projekte (`src/Core`, `src/Client`, das Meta-Paket
  `src/CalloraVoipSdk`) sowie die Beispiel-Apps sind **Multi-Target**: `net8.0;net9.0;net10.0`
  (aus den jeweiligen `.csproj`). Zum vollständigen Bauen genügt ein SDK, das das höchste Ziel
  (net10.0) bedienen und die niedrigeren Targets erzeugen kann. `net8.0` ist das
  LTS-Mindestziel — Consumer, die nur gegen net8.0 bauen, sind vollständig unterstützt.
- **Betriebssystem.** Der Core ist plattformneutral. Die Audio-Geräteanbindung ist
  betriebssystemspezifisch (`src/Audio/Linux`, `src/Audio/Windows`); die Beispiel-Apps
  referenzieren i. d. R. das Linux-Audio-Projekt (siehe unten).
- **Abhängigkeiten.** NuGet-Restore ist Pflicht vor dem ersten Build (u. a. BouncyCastle für
  DTLS/SHA-512-256 und Concentus für Opus — siehe
  [`../../adr/ADR-028-dtls-srtp-foundation.md`](../../adr/ADR-028-dtls-srtp-foundation.md),
  [`../../adr/ADR-049-opus-codec-integration-concentus.md`](../../adr/ADR-049-opus-codec-integration-concentus.md)).

---

## 2. Build, Test, Run

Aus dem Repository-Wurzelverzeichnis (`CalloraVoipSdk.sln` liegt dort):

```bash
# Abhängigkeiten wiederherstellen
dotnet restore CalloraVoipSdk.sln

# Gesamte Solution bauen
dotnet build CalloraVoipSdk.sln

# Alle Testprojekte ausführen
dotnet test CalloraVoipSdk.sln

# Beispiel-App (interaktiver SIP-Softphone-Demo) starten
dotnet run --project examples/CalloraVoipSdk.Sample.BasicCalling/CalloraVoipSdk.Sample.BasicCalling.csproj
```

Die Beispiel-App `CalloraVoipSdk.Sample.BasicCalling` fragt SIP-Server, Benutzer, Passwort und
Anzeigenamen interaktiv ab, registriert die Line, nimmt eingehende Anrufe an bzw. wählt
(`d <ziel>`) und routet Audio über das SDK-Default-Routing (`AttachDefaultAudioAsync`).
`-v`/`--verbose` schaltet SDK-Debug-/Trace-Logging frei. Einstiegspunkt:
`examples/CalloraVoipSdk.Sample.BasicCalling/Program.cs`.

> Tipp: Für gezielte Läufe einzelne Testprojekte statt der ganzen Solution testen (z. B.
> `dotnet test tests/CalloraVoipSdk.Client.Tests/CalloraVoipSdk.Client.Tests.csproj`) — die
> Interop-/Soak-Projekte brauchen zusätzliche Laufzeitumgebung (Docker-Gegenstellen) und laufen
> nicht als reine Unit-Läufe.

---

## 3. Solution-Struktur

`CalloraVoipSdk.sln` bündelt Produktivcode (`src/`), Beispiele (`examples/`), Tests (`tests/`)
und Benchmarks (`perf/`).

### Produktivprojekte (`src/`)

| Projekt | Pfad | Rolle |
| --- | --- | --- |
| `CalloraVoipSdk.Core` | `src/Core/CalloraVoipSdk.Core.csproj` | Eigenständiger SIP/RTP/SRTP/SDP/Media-Stack (Domain / Application / Infrastructure). Kein externer SIP-Stack zur Laufzeit. |
| `CalloraVoipSdk.Client` | `src/Client/CalloraVoipSdk.Client.csproj` | Öffentliche Fassaden: `VoipClient`, WebRTC-Fassade (`WebRtc/`), Server-Hosting (`Hosting/`), DI-Extensions (`Infrastructure/DependencyInjection/`). |
| `CalloraVoipSdk` (Meta) | `src/CalloraVoipSdk/CalloraVoipSdk.csproj` | Aggregierendes Consumer-Paket. |
| Audio-Adapter | `src/Audio/Linux`, `src/Audio/Windows` | Plattformspezifische Audio-Geräteanbindung (`LinuxAudioDevice`, `WindowsAudioDevice`). |

Der Core folgt DDD-Schichtung (`Domain/` · `Application/` · `Infrastructure/` · `Sdk/`);
`Infrastructure/*` ist internes Implementierungsdetail. Details und die maschinelle Durchsetzung
der Schichtregeln stehen in [`architecture.md`](architecture.md).

### Beispiele (`examples/`)

`BasicCalling`, `CustomAudio`, `Dialer`, `Switchboard`, `Transfer`, `VideoCalling` sowie die
WebRTC-Beispiele `WebRtcPeer`, `WebRtcDependencyInjection`, `WebRtcRecording` und
`WebRtcVideoCall.Web`. Jedes Beispiel referenziert `src/Client` + `src/Core` (die SIP-Beispiele
zusätzlich `src/Audio/Linux`).

### Tests (`tests/`) und Benchmarks (`perf/`)

`CalloraVoipSdk.Core.IntegrationTests`, `CalloraVoipSdk.Client.Tests`,
`CalloraVoipSdk.Audio.Tests`, `CalloraVoipSdk.ArchitectureTests` (Schichtregeln),
`CalloraVoipSdk.InteropTests` + `CalloraVoipSdk.InteropHarness` (Gegenstellen),
`CalloraVoipSdk.SoakTests`; Benchmarks unter `perf/`. Test-/Interop-/Soak-Strategie:
[`quality-and-testing.md`](quality-and-testing.md) und
[`../../adr/ADR-058-layered-test-interop-soak-model.md`](../../adr/ADR-058-layered-test-interop-soak-model.md).

### Einstiegspunkt: `VoipClient`

Die zentrale Runtime-Fassade ist **`VoipClient : IVoipClient`**
(`src/Client/Application/Facades/VoipClient.cs`, Interface
`src/Client/Application/Facades/IVoipClient.cs`). Kernfluss (siehe `Program.cs` der
BasicCalling-App):

1. `new VoipClient(new VoipConfiguration { ... })` — oder via DI (Abschnitt 4.3).
2. `await client.ConnectAsync(account, options)` → registrierte `IPhoneLine`.
   (`RegisterAndWaitAsync` existiert noch, ist aber `[Obsolete]` zugunsten `ConnectAsync`.)
3. Ausgehend: `await client.DialAndWaitUntilConnectedAsync(line, targetUri, ...)`.
   Eingehend: `line.IncomingCall` → `call.AcceptAsync()` / `call.HangupAsync()`.
4. Audio: `await client.AttachDefaultAudioAsync(call)`.

`IVoipClient` exponiert außerdem die für Erweiterungen relevanten Zugänge `Modules`
(`IModuleRegistry`) und `Media` (`IMediaManager`) — siehe Abschnitt 4.

---

## 4. Erweiterungspunkte

Alle vier Erweiterungspunkte sind **öffentliche, supported Seams** — sie kommen ohne Zugriff auf
`Infrastructure/*` aus.

### 4.1 Modul-/Plugin-Contract (`IVoipClientModule` / `ModuleRegistry`)

Der **In-Process-Modul-Seam** des SDK erlaubt optionalen Zusatzpaketen (z. B. einer Realtime-AI-
Bridge), sich zur Laufzeit an einen Client zu hängen, ohne dass der Core konkrete Modultypen
kennt.

- **Contract:** `IVoipClientModule` (`src/Client/Application/Modules/IVoipClientModule.cs`) —
  ein Marker mit `string ModuleId { get; }` und einem Default-No-op-Hook
  `void OnAttached(IVoipClient client)`. Modulpakete definieren ihre eigenen Feature-Interfaces
  *auf* diesem Marker; der SDK-Core referenziert keine konkreten Modultypen.
- **Registry:** `IModuleRegistry` (`src/Client/Application/Managers/IModuleRegistry.cs`,
  Implementierung `ModuleRegistry.cs`), thread-safe. `Register(module)` ruft zuerst `OnAttached`
  auf und macht das Modul **erst danach** auflösbar (kein teil-initialisierter Zustand).
  Auflösung per Feature-Vertrag: `T Get<T>()` (wirft `ModuleFeatureUnavailableException`, falls
  keins passt) bzw. `bool TryGet<T>(out T)`. Bei mehreren passenden Modulen gewinnt das zuerst
  registrierte.
- **Zugang:** `IVoipClient.Modules` (`src/Client/Application/Facades/IVoipClient.cs`). Register
  zur Laufzeit über den Client oder — DI-nativ — als registrierter `IVoipClientModule`-Service
  (Abschnitt 4.3), der beim Client-Aufbau automatisch angehängt wird.

Design und Motivation: die Verankerung als erster Modul-Consumer-Seam beschreibt
[`../../adr/ADR-059-public-media-tap-contract.md`](../../adr/ADR-059-public-media-tap-contract.md);
die Plattform-/Store-Perspektive
[`../../adr/ADR-007-host-centric-platform-split.md`](../../adr/ADR-007-host-centric-platform-split.md)
und [`../../adr/ADR-008-community-module-store.md`](../../adr/ADR-008-community-module-store.md).

> **Abgrenzung.** Der oben beschriebene `IVoipClientModule`-Seam ist der **In-Process-Modulseam
> des SDK**. Davon zu unterscheiden ist der **Host-seitige Plugin-Contract** (Lifecycle
> install/activate/deactivate/uninstall, `IHostManagedPlugin`/`ICalloraRuntimePlugin`,
> Compliance-Manifest) für die separate Host-/CPaaS-Schicht — dokumentiert in
> [`../../reference/plugin-contract.md`](../../reference/plugin-contract.md). Beide Ebenen sind
> bewusst getrennt (ADR-007).

### 4.2 Media-Tap (`IMediaReceiver` / `IMediaSender`)

Der **Per-Call-Media-Tap** ist der supported Weg, auf einer laufenden Verbindung eingehende
Medien zu beobachten und ausgehende einzuspeisen — ohne Griff in `Infrastructure/*`.

- **Inbound:** `IMediaReceiver` (`src/Core/Application/Media/IMediaReceiver.cs`) — Event
  `FrameReceived` feuert **synchron auf dem Medienpfad** für *jeden* eingehenden Frame; Handler
  dürfen **nicht blockieren und keine Inline-I/O** machen (in eigene Queue puffern, sofort
  zurückkehren). Mehrere Receiver können parallel an denselben Call gehängt werden; jeder sieht
  jeden Frame. `AttachToCall(ICall)` / `Detach()`.
- **Outbound:** `IMediaSender` (`src/Core/Application/Media/IMediaSender.cs`) — `SendAsync(frame, ct)`
  speist einen bereits **im ausgehandelten Codec kodierten** Frame ein (Payload-Type/Clock-Rate
  aus `ICall.MediaParameters`; Codec-Handling ist **transport-only** — das SDK kodiert/dekodiert
  im allgemeinen Medienpfad nicht selbst). Frames außerhalb `Connected`/`OnHold` werden verworfen.
- **Fabrik & Zugang:** `IMediaManager.CreateReceiver()` / `CreateSender()`
  (`src/Core/Application/Media/IMediaManager.cs`), erreichbar über `IVoipClient.Media`. Für Video
  analog `CreateVideoReceiver()` / `CreateVideoSender()` (ebenfalls transport-only).

Frame-Modell, Fan-out und Threading-Vertrag:
[`../../adr/ADR-059-public-media-tap-contract.md`](../../adr/ADR-059-public-media-tap-contract.md);
Medien-Thread-Landkarte: [`../../maintainers/threading-map.md`](../../maintainers/threading-map.md).

### 4.3 DI-Registrierung (`AddCalloraVoip`)

Fassaden werden über `Microsoft.Extensions.DependencyInjection` in Host/DI aufgebaut:

```csharp
services.AddCalloraVoip(o => { /* VoipOptions */ });
```

- `AddCalloraVoip` (`src/Client/Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`)
  registriert `IVoipClient`/`VoipClient` als Singleton (Options-basiert, `ValidateOnStart`),
  hängt einen `IHostedService` für den Lebenszyklus ein und gibt einen `CalloraBuilder`
  (`src/Client/Infrastructure/DependencyInjection/VoipSdkBuilder.cs`) für weitere Verdrahtung
  zurück.
- DI-registrierte `IVoipClientModule`-Services werden beim Client-Aufbau automatisch angehängt
  (der `IServiceProvider` wird in den `VoipClient` gereicht) — die DI-native Variante zu 4.1.
- `LoggerFactory` wird aus den Options oder dem DI-`ILoggerFactory` bezogen.

Vollständige Config-Oberfläche und Empfehlungen: [`architecture.md`](architecture.md) sowie
[`../../adr/ADR-006-api-versioning-strategy.md`](../../adr/ADR-006-api-versioning-strategy.md) für
die API-Versionierungspolitik.

### 4.4 WebRTC-Fassade und Server-Hosting-Fassade

**WebRTC-Peer-Fassade.** `IWebRtcClient` / `WebRtcClient` (`src/Client/WebRtc/`) ist die
eigenständige, browser-orientierte Peer-Fassade (`CreatePeer()`, `IPeerConnection`,
`TrackReceived`-Modell). DI:

```csharp
services.AddCalloraWebRtc(o => { /* WebRtcOptions */ });
```

`AddCalloraWebRtc` (`src/Client/WebRtc/WebRtcServiceCollectionExtensions.cs`) registriert die
Fassade und gibt einen `CalloraWebRtcBuilder` (`src/Client/WebRtc/CalloraWebRtcBuilder.cs`) zurück
— fluent u. a. `WithStunServer(host, port?)`, `WithTurnServer(host, user, pass, port?, transport?)`,
`WithIceServers(...)` (akkumulierend). Eine Pure-WebRTC-App ruft nur `AddCalloraWebRtc`; ein Host
mit beiden Fassaden kettet `AddCalloraVoip(...)` und `AddCalloraWebRtc(...)`. WebRTC hat einen
eigenen Modulseam (`IWebRtcClientModule` / `WebRtcModuleRegistry`).

**Server-Hosting-Fassade.** Der Namespace `CalloraVoipSdk.Hosting` (`src/Client/Hosting/`) macht
den eingebauten TURN-/STUN-Server als gehostete Dienste nutzbar:

```csharp
services.AddCalloraTurnServer(b => b.WithBindEndPoint(...).WithPublicRelayAddress(...));
services.AddCalloraStunServer(b => b.WithBindEndPoint(...).WithTransport(...));
```

`AddCalloraTurnServer` (`src/Client/Hosting/TurnServerServiceCollectionExtensions.cs`,
Builder `CalloraTurnServerBuilder`) und `AddCalloraStunServer`
(`src/Client/Hosting/StunServerServiceCollectionExtensions.cs`) registrieren `ITurnServerHost` /
`IStunServerHost`; Builder u. a. `WithBindEndPoint`, `WithTransport`, `WithTlsCertificate`,
`WithLoggerFactory` (TURN zusätzlich `WithPublicRelayAddress`).

Roadmap, Fassaden-Vollständigkeit und der TURN-Relay-Pfad:
[`../../adr/ADR-012-webrtc-public-facade.md`](../../adr/ADR-012-webrtc-public-facade.md),
[`../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md`](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md),
[`../../adr/ADR-009-webrtc-browser-peer-roadmap.md`](../../adr/ADR-009-webrtc-browser-peer-roadmap.md).

---

## 5. Doku- und Wartungs-Einstiege

- **Architekturentscheidungen:** [`../../adr/`](../../adr/) — 61 nummerierte ADRs
  (`README.md`/`toc.yml` als Index). Beginn: ADR-014 (DDD-Schichtung), ADR-006 (API-Versionierung).
- **Referenzen:** [`../../reference/`](../../reference/) — u. a.
  [`plugin-contract.md`](../../reference/plugin-contract.md),
  [`semver-policy.md`](../../reference/semver-policy.md),
  [`websocket-protocol.md`](../../reference/websocket-protocol.md),
  [`decision-inventory.md`](../../reference/decision-inventory.md).
- **Maintainer-Wissen:** [`../../maintainers/`](../../maintainers/) —
  [`onboarding-debugging.md`](../../maintainers/onboarding-debugging.md) (geführter Einstieg),
  [`flows.md`](../../maintainers/flows.md) (die tragenden Fluss-Walkthroughs),
  [`threading-map.md`](../../maintainers/threading-map.md) (Threads/Locks/Dispose-Ordnung),
  [`repo-setup.md`](../../maintainers/repo-setup.md) (GitHub-Einrichtung).
- **API-/Doku-Portal (DocFX):** `docfx.json` in der Repo-Wurzel erzeugt aus `src/Core` +
  `src/Client` (XML-Doku) plus dem Prosa-Portal unter `docs/portal/` ein statisches Site
  (`_site/`). Build: `docfx docfx.json` (bzw. `--serve` zur lokalen Vorschau). Getting-Started,
  Guides, Concepts und die Production-Runbooks liegen unter `docs/portal/`.
