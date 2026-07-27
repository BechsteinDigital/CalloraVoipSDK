# Messergebnis

Stand: 27. Juli 2026

## Reproduzierbarer Lauf mit Ozeki SDK Linux 10.5.1

Umgebung:

- .NET SDK 10.0.202 / Runtime 10.0.6
- SIPSorcery 10.0.12
- Ozeki.SDK.Linux 10.5.1 für `net10.0`
- Ozeki-`.deb`: 66.195.866 Bytes, SHA-256
  `599a2e6040acee0f0dbf3f29946fd6b18d4837db69b67b4832179e922df09170`
- `Ozeki.SDK`-NuGet 10.5.1, SHA-256
  `16f7dc88115255625ec265c650027a865c6ee8a63e196b47249cd5a761b429c1`
- `Ozeki.SDK.Linux`-NuGet 10.5.1, SHA-256
  `a8a3f67cf52925a24e1fdc9e3f439b653e46f5c68bedae2040030bbe3a908ace`
- Asterisk-Container `andrius/asterisk:22`
- Callora-Checkout auf Commit
  `401906986164b07d4f88e7ecb3dfebc4a496d5d6`

Der Callora-Checkout lief in einem eigenen Worktree auf Basis des nach der
Handover-Historienbereinigung neu geschriebenen `origin/main`. Unter `src/`
gab es keine lokalen Änderungen; die Vergleichsquellen liegen als manuell
gestartetes Projekt unter `tests/CalloraVoipSdk.CompetitorInteropTests`.

Ausgeführt wurde:

```bash
./run-interop.sh
```

Ergebnis des vollständigen Laufs:

```text
Passed! - Failed: 0, Passed: 42, Skipped: 0, Total: 42
Duration: 2 m 17 s
```

| Szenario | Callora | Ozeki 10.5.1 | SIPSorcery |
|---|---:|---:|---:|
| Registrierung | PASS | PASS | PASS |
| Outbound + RTP | PASS | PASS | PASS |
| Inbound + Answer + RTP | PASS | PASS | PASS |
| No-Answer-Timeout | PASS | PASS | PASS |
| RFC-4733-DTMF | PASS | PASS | PASS |
| WAV-Playback | PASS | PASS | PASS |
| WAV-Recording | PASS | PASS | PASS |
| Medien-Bridge | PASS | PASS | PASS |
| Hold/Unhold + SDP + RTP-Resume | PASS | PASS | PASS |
| 486/403/404 + Cleanup + erfolgreicher Folgeanruf | PASS | PASS | PASS |
| Caller-Cancellation: `Canceled` + erfolgreicher Folgeanruf | PASS | PASS | PASS |
| Caller-Cancellation: SIP-`CANCEL` + externer Channel-Cleanup | **FAIL** | PASS | PASS |
| Call- und Registration-Cleanup | PASS | PASS | PASS |

Die 42/42 beziehen sich auf ausführbare Charakterisierungstests. Der
unterschiedliche Cleanup-Vertrag ist darin absichtlich stack-spezifisch
hinterlegt; das **FAIL** in der Ergebnismatrix wird dadurch nicht zu einem
Parity-PASS umgedeutet.

Auf dem dokumentierten, bereinigten Main-Commit bestand der neue
Hold/Unhold-Slice im gezielten Staging-Lauf, im vollständigen Lauf und nach
Übernahme in den kanonischen Ordner mit insgesamt 9/9 erfolgreichen
Stack-Ausführungen.

Die Remote-Rejection-Matrix bestand im gezielten Lauf und im vollständigen
Lauf mit 18/18 erfolgreichen Stack-Ausführungen. In jedem Fall endete die
Ablehnung vor dem Zehn-Sekunden-Connect-Timeout, Asterisk meldete anschließend
null aktive Channels, die Registrierung blieb bestehen und ein Folgeanruf
empfing wieder RTP.

Die Caller-Cancellation bestand als Charakterisierung im gezielten Lauf mit
3/3 und danach im vollständigen Lauf. Alle Stacks meldeten innerhalb von acht
Sekunden `Canceled`, behielten ihre Registrierung und schafften anschließend
einen RTP-führenden Folgeanruf. Ozeki und SIPSorcery sendeten dabei ein auf
Asterisk sichtbares SIP-`CANCEL` und räumten den Channel auf. Callora tat
beides nicht: Der `noanswer`-Channel blieb länger als fünf Sekunden bestehen.
Der vor der Charakterisierung verwendete identische Parity-Assert scheiterte
für Callora reproduzierbar in zwei von zwei Läufen, darunter einmal noch nach
acht Sekunden.

## Stack-spezifische Codefläche

Gezählt wurden nichtleere physische Zeilen der funktionalen C#-Adapter, ohne
gemeinsamen Vertrag, Asterisk-Fixture, Tests und gemeinsame
Tondatei-Erzeugung.

| Stack | Zugeordnete Dateien | Nichtleere Zeilen |
|---|---|---:|
| Callora | `Adapters/CalloraStack.cs` | 298 |
| Ozeki 10.5.1 | `Adapters/OzekiStack.cs` | 507 |
| SIPSorcery | `Adapters/SipSorceryStack.cs` + `Adapters/SipSorceryPcmuWaveCodec.cs` | 679 |

Für genau diesen Slice benötigt Ozeki damit rund **1,70-mal** und SIPSorcery
rund **2,28-mal** so viel funktionalen Adaptercode wie Callora. Das sind keine
allgemeinen Bibliotheksmetriken, sondern Messwerte dieses Vertrags.

Nur für den neuen Hold/Unhold-Vertrag kamen stack-spezifisch drei nichtleere
Zeilen bei Callora, 14 bei Ozeki und 13 bei SIPSorcery hinzu. Callora reicht
`ICall.State`, `HoldAsync` und `UnholdAsync` direkt durch. Ozeki und SIPSorcery
stellen die Funktion ebenfalls öffentlich bereit; ihre synchron ausgelösten
Operationen und stack-spezifischen Hold-Zustände mussten im gemeinsamen
awaitbaren Vertrag zusätzlich adaptiert werden.

Für die neue Remote-Rejection-/Recovery-Matrix war keine zusätzliche
stack-spezifische Adapterzeile erforderlich. Alle drei vorhandenen Adapter
erfüllten den normalisierten `Failed`- und Wiederverwendbarkeitsvertrag; neu
hinzu kamen ausschließlich gemeinsamer Dialplan und gemeinsame Assertions.

Für den Cancellation-Vertrag kamen gegenüber diesem Stand fünf nichtleere
Zeilen bei Callora, zwei bei Ozeki und vier bei SIPSorcery hinzu. Callora
reicht den bereits modellierten `DialStatus.Canceled` durch. Ozeki und
SIPSorcery normalisieren ihren Cancellation-Pfad und stoßen den expliziten
Call-Cleanup an.

Der Vertrag wurde ursprünglich anhand des alten Dialer-/Callora-Slice
formuliert. Die Asterisk-Beobachtungen sind externe
Verhaltensbeobachtungen; der Codeflächenvorteil kann dagegen auch ausdrücken,
dass Calloras öffentliche Abstraktionen bereits genau zu diesem Vertrag
passen. Er ist deshalb ein belastbarer Fit-Messwert, aber kein vollständig
herstellerneutraler Ergonomie-Benchmark.

Ozekis reproduzierbarer Linux-Weg umfasst zusätzlich:

| Operativer Bestandteil | Nichtleere Zeilen |
|---|---:|
| Runner einschließlich `.deb`-NuGet-Extraktion | 69 |
| Enger `/usr/share/Ozeki.{…}`-Pfadshim in C | 183 |
| Summe | 252 |

Dieser Aufwand ist nicht in den 505 funktionalen Ozeki-Zeilen versteckt. Bei
einer systemweiten Paketinstallation entfällt die Extraktion, nicht aber
automatisch das Berechtigungsproblem des Runtime-Verzeichnisses.

## Ozeki alt gegen neu

| Merkmal | Historisch 1.8.23.0 | Linux 10.5.1 |
|---|---|---|
| Zielplattform | .NET Framework 4.5.2 | .NET 10 |
| Funktionsadapter | 536 Zeilen | 491 Zeilen |
| WAV → PCMU | eigener Adapter über Ozekis `CodecG711uLaw` | direkter Ozeki-Medienpfad |
| `System.Drawing.Common`-Workaround | erforderlich | nicht erforderlich |
| `/usr/share/Ozeki.{…}`-Redirect | erforderlich | weiterhin erforderlich |
| Ergebnis | 9/9 PASS | 9/9 PASS |

Die aktuelle SDK reduziert den funktionalen Ozeki-Adapter um 45 nichtleere
Zeilen beziehungsweise rund 8,4 Prozent. Vor allem entfällt der fachfremde
Windows-/Legacy-Ballast. Das feste Linux-Lizenz-/Trial-Verzeichnis bleibt
hingegen unverändert problematisch.

Historische Referenz:

- OzekiSDK 1.8.23.0: 57.634.304 Bytes
- SHA-256
  `981e25c0216ee0c6f12577e0faaf644553c78f439d499ab81317de6d24c1c33c`
- damaliger vollständiger Dreierlauf: 27/27 PASS

## Beobachtete Unterschiede

Callora:

- Playback, Recording und Media-Cross-Connect sind direkt verfügbare
  SDK-Funktionen.
- Dial-Timeout und Ergebnisstatus sind bereits als Domänenresultat modelliert.
- Registrierungs-Cleanup ist explizit awaitbar.
- Derselbe Adapter kombiniert Managed Workflows mit tieferen öffentlichen
  Verträgen: `ICall`, ausgehandelte Medienparameter,
  `IMediaReceiver`/`IMediaSender` und `MediaConnector`.
- Die kleine Codefläche entsteht damit nicht durch eine reine Blackbox:
  Call-Zustand, codierte Frames und Medienrouting bleiben für
  produktspezifische Logik erreichbar.
- Der Adapter bleibt überwiegend eine Zuordnung zwischen Vergleichsvertrag und
  SDK.
- Hold/Unhold ist als awaitbarer `ICall`-Workflow einschließlich typisiertem
  `OnHold`-Zustand direkt verfügbar. Asterisk beobachtete `sendonly`/`inactive`
  und anschließend `sendrecv`; RTP lief nach Unhold weiter.
- 486, 403 und 404 werden ohne Exception als `DialStatus.Failed` beendet. Der
  Client blieb registriert und war unmittelbar für einen erfolgreichen
  Folgeanruf wiederverwendbar.
- Eine caller-seitige Cancellation wird prompt als `DialStatus.Canceled`
  zurückgegeben; dieselbe Registrierung bleibt für einen erfolgreichen
  Folgeanruf verwendbar.
- Nachteil im geprüften Managed Workflow: Erfolgt die Cancellation während
  `PhoneLine.DialAsync`, sendet der Client kein SIP-`CANCEL`. Der Asterisk-
  Channel blieb länger als fünf beziehungsweise im Wiederholungslauf acht
  Sekunden bestehen, obwohl der lokale Workflow schon beendet war.
- Nachteil des geprüften öffentlichen `DialResult`: Die drei unterschiedlichen
  SIP-Antworten werden zu `Failed` zusammengefasst; ein Remote-Statuscode ist
  dort nicht verfügbar. Für statusabhängige Retry-/Routing-Policies fehlt
  damit auf dieser Komfortebene noch Granularität.

Der offene PR
[#105](https://github.com/BechsteinDigital/callora-voip-sdk/pull/105)
schlägt für den zuletzt genannten Punkt einen öffentlichen
`CallTerminationReason` vor. Er gehört nicht zum gemessenen Main-Stand und
war bei dieser Auswertung noch nicht validiert: Sein Interop-Check scheiterte
im Busy-Test an einem nicht gesetzten Reason. Der PR betrifft nicht den hier
beobachteten fehlenden SIP-`CANCEL` bei caller-seitiger Cancellation.

Ozeki SDK Linux 10.5.1:

- Softphone, Dialoge, DTMF, WAV-Playback/-Aufnahme und Media-Connector sind als
  High-Level-Bausteine vorhanden; Ozeki liegt konzeptionell deutlich näher an
  Callora als an der hier nötigen SIPSorcery-Integration.
- Die vorhandene API ist weitgehend kompatibel zur historischen Version.
- `IPhoneCall.Hold()`/`Unhold()` und die Zustände `LocalHeld`/`InactiveHeld`
  bestanden denselben Asterisk-Vertrag. Für den gemeinsamen async-Vertrag
  bleibt eine ereignis-/zustandsbasierte Await-Schicht Anwendungsaufgabe.
- Call- und Registrierungszustände müssen weiterhin ereignisbasiert in
  awaitbare Operationen übersetzt werden.
- Alle drei Remote-Ablehnungen wurden extern sauber beendet und ein Folgeanruf
  funktionierte. `CallStateChangedArgs` stellt zusätzlich Statuscode,
  `CallError` und Reason bereit; diese Granularität normalisiert der
  Vergleichsadapter bewusst weg.
- Bei caller-seitiger Cancellation löste der Adapter `HangUp()` aus. Asterisk
  sah das SIP-`CANCEL`, räumte den Channel auf und der Folgeanruf funktionierte.
- Der native .NET-10-Medienpfad funktioniert ohne `System.Drawing.Common` und
  ohne den früheren DMO/G.711-Workaround.
- Trotz eigenem Linux-Paket setzt der Runtime-Start Schreibzugriff auf einen
  festen `/usr/share`-Pfad voraus. Ohne Redirect scheitert bereits die
  Softphone-Erzeugung als normaler Benutzer.
- Die SDK bleibt proprietär und bringt mehrere Ozeki-Paketabhängigkeiten mit.

SIPSorcery:

- Registrierung, User-Agent pro Dialog und `MediaSession` sind explizit und
  gut kontrollierbar.
- `SIPUserAgent.PutOnHold()`/`TakeOffHold()` und `IsOnLocalHold` bestanden
  denselben Asterisk-Vertrag; die öffentliche User-Agent-Operation ist
  synchron ausgelöst und wird vom Adapter in den awaitbaren Vertrag übersetzt.
- PCMU-Quelle/-Senke, RTP-Taktung, µ-law-Konvertierung, WAV-Recording und
  Cross-Connect müssen für diesen Slice in der Anwendung gebaut werden.
- Für saubere Deregistrierung genügt `Stop(true)` allein im Dispose-Pfad nicht:
  Der Adapter muss auf `RegistrationRemoved` warten, bevor er den
  `SIPTransport` beendet.
- Die Remote-Ablehnungen wurden explizit durch Schließen der MediaSession und
  Dispose des User-Agents bereinigt; der Folgeanruf funktionierte.
  `ClientCallFailed` kann zusätzlich die SIP-Response liefern, wird im
  gemeinsamen `Failed`-Vertrag aber nicht ausgewertet.
- Bei caller-seitiger Cancellation muss die Anwendung `Cancel()`, das
  Beobachten des laufenden Call-Tasks sowie MediaSession- und User-Agent-
  Cleanup selbst orchestrieren. Damit waren SIP-`CANCEL`, Channel-Cleanup und
  der Folgeanruf erfolgreich.
- Die zusätzliche Arbeit liefert viel Low-Level-Kontrolle, vergrößert aber
  Codefläche und Lifecycle-Verantwortung.

## Bewertung

Alle drei Stacks sind für den geprüften technischen Kern funktional geeignet.
Callora hat in diesem Slice weiterhin die kleinste Integrationsfläche. Das ist
ein belastbarer Produkt-Fit- und Wartungsvorteil für einen Dialer, dessen
Differenzierung in Kampagnenlogik, Agenten-Orchestrierung und Betrieb liegt.
Zusätzlich ist belegt, dass dieser Komfort im geprüften Ausschnitt nicht mit
dem Verlust tieferer Kontrolle erkauft wird: Der gleiche öffentliche
Callora-Vertrag deckt Happy Path, direkten Call-Zustand, Frame-Zugriff,
Frame-Injection und Bridging ab.

Die Fehler-/Lifecycle-Erweiterung zeigt außerdem: Alle drei Stacks verkraften
486, 403 und 404 ohne verwaisten Asterisk-Channel oder vergiftete
Registrierung. Calloras Managed Workflow benötigt dafür keinen zusätzlichen
Adaptercode. Der Vorteil ist ein einfacher, wiederverwendbarer Lebenszyklus;
der konkrete Nachteil ist die zu grobe Fehlerursache auf `DialResult`.

Beim aktiven Abbruch ist das Bild differenzierter: Callora besitzt bereits
den passenden Domänenstatus und bleibt lokal wiederverwendbar, erfüllt aber
im geprüften Timing seine externe SIP-Cleanup-Verantwortung nicht. Ozeki und
SIPSorcery benötigen etwas mehr Adapterlogik, senden dafür `CANCEL` und
beenden den Remote-Channel. Für Dialer ist das ein relevanter Call-Lifecycle-
Nachteil von Callora, nicht nur eine kosmetische Statusdifferenz.

Ozeki 10.5.1 ist gegenüber dem historischen Bestand deutlich aufgewertet:
native .NET-10-Pakete, direkter Medienpfad und weniger Adaptercode. Funktional
liegt es in diesem Slice eng bei Callora. Seine größten Nachteile sind hier
nicht SIP- oder Medienfunktionalität, sondern das proprietäre Paketmodell, die
ereignisbasierte Lifecycle-Integration und der weiterhin fehleranfällige feste
Linux-Runtime-Pfad.

SIPSorcery besteht ebenfalls alles und bietet die transparenteste
Low-Level-Kontrolle. Der Preis ist in diesem Slice die größte eigene
Medien- und Lifecycle-Implementierung.

Bewiesen ist damit nicht, dass nur Callora diese Aufgaben lösen kann. Bewiesen
ist enger: Alle drei lösen denselben realen SIP-/RTP-Vertrag einschließlich
lokalem Hold/Unhold und terminalen Remote-Ablehnungen; Callora tut es hier mit
der kleinsten funktionalen Integrationsfläche, während Ozeki 10.5.1 den
Abstand zur historischen Version sichtbar verkleinert. Die neuen Slices
stärken das Progressive-API-Argument: Calloras Managed Dial und tieferes
`ICall`-Verhalten lassen sich ohne Abstraktionsbruch kombinieren. Sie zeigen
aber auch konkrete Lücken bei detaillierten Remote-Fehlerursachen und beim
SIP-Cleanup eines aktiv abgebrochenen Wahlversuchs. Noch nicht bewiesen ist
eine generelle Überlegenheit der übrigen Escape Hatches: Transfer,
In-Dialog-SIP, Custom-Header, Telemetrie, eigene Devices, Module, ICE und
WebRTC wurden in diesem Dreiervergleich nicht systematisch gegenübergestellt.
