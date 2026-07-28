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
- ursprünglicher 48/48-Basislauf mit Callora auf Commit
  `80f5e20067d40c2d459d2c68372fdf6dfb282d96`
- Zwischenvalidierung von Cancellation und Termination-Reasons auf
  `be0eb18e` (`origin/main`, PR #105 und #107 gemergt)
- aktueller vollständiger 48/48-Lauf auf
  `37d7c803` (`origin/main`, Cancellation-Follow-up und PR #114 gemergt)

Der Callora-Checkout lief in einem eigenen Worktree auf Basis des nach der
Handover-Historienbereinigung neu geschriebenen `origin/main`. Unter `src/`
gab es keine lokalen Änderungen; die Vergleichsquellen liegen als manuell
gestartetes Projekt unter `tests/CalloraVoipSdk.CompetitorInteropTests`.

Ausgeführt wurde:

```bash
./run-interop.sh
```

Ergebnis des aktuellen vollständigen Laufs:

```text
Passed! - Failed: 0, Passed: 48, Skipped: 0, Total: 48
Duration: 3 m 27 s
```

Die Zwischenvalidierung auf `be0eb18e` ergab:

- gemeinsamer 486/403/404-Reason-Vertrag: 9/9 PASS
- kanonischer Callora-Asterisk-Reason-Block auf .NET 9 und .NET 10:
  jeweils 6/6 PASS; darunter 403, 404 und Remote-BYE
- temporär auf Parität gestellte Cancellation-Assertion: 2/3 PASS;
  ausschließlich Callora scheiterte weiterhin an fehlendem Wire-`CANCEL`
- zwei vollständige Wiederholungsläufe: 45/48 und 47/48; die jeweils
  wechselnden Fehler betrafen PBX-Recovery beziehungsweise einmal
  SIPSorcery-Hold, nicht den neuen Reason-Vertrag
- direkter Wiederholungslauf der betroffenen PBX-/Hold-Szenarien: 6/6 PASS

Die abschließende Nachvalidierung auf `37d7c803` ergab:

- unveränderter alter Cancellation-Charakterisierungstest zunächst erwartbar
  rot für Callora, weil tatsächlich `CANCEL` beobachtet wurde statt des alten
  erwarteten `false`
- einheitlicher Cancellation-Parity-Vertrag danach 3/3 PASS: `Canceled`,
  Callora-Reason `Canceled`/487, Wire-`CANCEL`, null Asterisk-Channels und
  RTP-Folgeanruf
- kanonischer Callora-Asterisk-Cancellation-Test: 1/1 PASS
- vollständiger Dreiervergleich: 48/48 PASS in 3 min 27 s

Die früher wechselnden Full-Run-Fehler waren damit kein reproduzierbarer
Stack-Funktionsverlust. Sie bleiben dennoch ein Hinweis, Recovery-/Hold-Gates
mit Wiederholungen und besseren Zeitreihen zu härten.

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
| 486/403/404 + Status/Kategorie + Cleanup + erfolgreicher Folgeanruf | PASS | PASS | PASS |
| Caller-Cancellation: `Canceled` + erfolgreicher Folgeanruf | PASS | PASS | PASS |
| Caller-Cancellation: SIP-`CANCEL` + externer Channel-Cleanup | PASS | PASS | PASS |
| Remote-BYE + Cleanup + erfolgreicher Folgeanruf | PASS | PASS | PASS |
| PBX-Ausfall innerhalb 20 s öffentlich als Registrierungsverlust sichtbar | PASS | **FAIL** | PASS |
| Automatische Wiederanmeldung + RTP-Folgeanruf nach PBX-Neustart | PASS | PASS | PASS |
| Call- und Registration-Cleanup | PASS | PASS | PASS |

Die 48/48 beziehen sich auf ausführbare Vergleichstests. Nur die öffentliche
Outage-Erkennung bleibt als stack-spezifische Charakterisierung hinterlegt;
das **FAIL** bei Ozeki wird dadurch nicht zu einem Parity-PASS umgedeutet.

Auf dem dokumentierten, bereinigten Main-Commit bestand der neue
Hold/Unhold-Slice im gezielten Staging-Lauf, im vollständigen Lauf und nach
Übernahme in den kanonischen Ordner mit insgesamt 9/9 erfolgreichen
Stack-Ausführungen.

Die Remote-Rejection-Matrix bestand zunächst als Cleanup-/Recovery-Vertrag und
auf `be0eb18e` zusätzlich als gezielter Reason-Vertrag mit 9/9
Stack-Ausführungen. Alle drei Stacks lieferten 486, 403 und 404 über ihre
öffentliche Oberfläche; der gemeinsame Vertrag normalisierte sie zu
`Busy`, `Rejected` und `Failed`. In jedem Fall endete die Ablehnung vor dem
Zehn-Sekunden-Connect-Timeout, Asterisk meldete anschließend null aktive
Channels, die Registrierung blieb bestehen und ein Folgeanruf empfing wieder
RTP.

Die Caller-Cancellation dokumentierte zunächst den realen Callora-Nachteil:
lokal `Canceled`/`Terminated`, aber kein Wire-`CANCEL` und ein weiter aktiver
Asterisk-Channel. Nach dem Follow-up in `37d7c803` besteht derselbe Vertrag
ohne stack-spezifische Ausnahme 3/3. Alle Stacks melden innerhalb von acht
Sekunden `Canceled`, senden ein Asterisk-sichtbares SIP-`CANCEL`, räumen den
Channel auf, behalten ihre Registrierung und schaffen anschließend einen
RTP-führenden Folgeanruf. Callora liefert zusätzlich einen öffentlichen
`TerminationReason` mit Kategorie `Canceled` und SIP-Status 487.

Der Remote-BYE-Slice bestand im gezielten Lauf mit 3/3 und danach im
vollständigen Lauf. Asterisk beendete jeweils ein bereits aufgebautes Gespräch
nach drei Sekunden mit einem auf dem Wire sichtbaren `BYE`. Alle drei Stacks
wechselten über ihre öffentliche Call-Oberfläche in den getrennten Zustand,
meldeten null aktive Calls, behielten ihre Registrierung und empfingen im
Folgeanruf erneut RTP. Asterisk meldete zwischen beiden Gesprächen null aktive
Channels.

Der PBX-Restart bestand als Charakterisierung im gezielten Lauf mit 3/3 und
danach im vollständigen Lauf. Vor dem Stopp wurde Asterisks persistierter
Contact-Speicher geleert; ein Contact nach dem Neustart beweist daher eine
neue Registrierung und nicht bloß wieder geladenen Zustand. Bei zehn Sekunden
Registrierungslebensdauer machte Callora den Verlust nach rund 5,1 Sekunden
und SIPSorcery nach rund 15,0 Sekunden öffentlich sichtbar. Ozekis
`IPhoneLine.RegState` blieb während des 20-Sekunden-Fensters auf
`RegistrationSucceeded`. Trotzdem meldeten sich alle drei automatisch wieder
an: nach Asterisk-Bereitschaft benötigten Callora rund 1,4 Sekunden, Ozeki
rund 3,4 Sekunden und SIPSorcery rund 1,6 Sekunden bis zum neuen Contact.
Anschließend bestand jeder Stack wieder einen RTP-führenden Folgeanruf.

## Stack-spezifische Codefläche

Gezählt wurden nichtleere physische Zeilen der funktionalen C#-Adapter, ohne
gemeinsamen Vertrag, Asterisk-Fixture, Tests und gemeinsame
Tondatei-Erzeugung.

| Stack | Zugeordnete Dateien | Nichtleere Zeilen |
|---|---|---:|
| Callora | `Adapters/CalloraStack.cs` | 332 |
| Ozeki 10.5.1 | `Adapters/OzekiStack.cs` | 518 |
| SIPSorcery | `Adapters/SipSorceryStack.cs` + `Adapters/SipSorceryPcmuWaveCodec.cs` | 697 |

Für genau diesen Slice benötigt Ozeki damit rund **1,56-mal** und SIPSorcery
rund **2,10-mal** so viel funktionalen Adaptercode wie Callora. Das sind keine
allgemeinen Bibliotheksmetriken, sondern Messwerte dieses Vertrags.

Nur für den neuen Hold/Unhold-Vertrag kamen stack-spezifisch drei nichtleere
Zeilen bei Callora, 14 bei Ozeki und 13 bei SIPSorcery hinzu. Callora reicht
`ICall.State`, `HoldAsync` und `UnholdAsync` direkt durch. Ozeki und SIPSorcery
stellen die Funktion ebenfalls öffentlich bereit; ihre synchron ausgelösten
Operationen und stack-spezifischen Hold-Zustände mussten im gemeinsamen
awaitbaren Vertrag zusätzlich adaptiert werden.

Die erste Remote-Rejection-/Recovery-Matrix benötigte keine zusätzliche
stack-spezifische Adapterzeile. Die spätere Reason-Nachvalidierung fügte
dagegen 25 nichtleere Zeilen bei Callora, sechs bei Ozeki und 18 bei
SIPSorcery hinzu: Callora mappt seinen neuen `ICall.TerminationReason`,
Ozeki `CallStateChangedArgs.StatusCode/Reason/Error` und SIPSorcery
`ClientCallFailed` samt `SIPResponse` in denselben Snapshot-Vertrag.

Für den Cancellation-Vertrag kamen gegenüber diesem Stand fünf nichtleere
Zeilen bei Callora, zwei bei Ozeki und vier bei SIPSorcery hinzu. Callora
reicht den bereits modellierten `DialStatus.Canceled` durch. Ozeki und
SIPSorcery normalisieren ihren Cancellation-Pfad und stoßen den expliziten
Call-Cleanup an.

Der Remote-BYE-Vertrag benötigte keine zusätzliche nichtleere Adapterzeile.
Alle drei Adapter konnten den bereits vorhandenen öffentlichen Call-Zustand
auswerten. Beim Callora-Adapter wurde die bestehende `ActiveCallCount`-
Definition ohne Größenänderung an die beiden anderen Adapter angeglichen:
Gezählt werden verbundene statt lediglich noch vom Testadapter gehaltene
Wrapper.

Der PBX-Recovery-Vertrag fügte neun nichtleere Zeilen bei Callora, fünf bei
Ozeki und keine bei SIPSorcery hinzu. Callora konfiguriert kurze
`RegistrationExpiry`- und `ReregisterOptions` und wertet den bestehenden
`IPhoneLine.State` aus. Ozeki benötigt eine
`PhoneLineConfiguration` mit `ExpirationTime` und
`RegisterBeforeExpires`. SIPSorcery hatte die entsprechenden
Konstruktorparameter bereits im Adapter.

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

Dieser Aufwand ist nicht in den 518 funktionalen Ozeki-Zeilen versteckt. Bei
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
- 486, 403 und 404 werden ohne Exception als `DialStatus.Failed` beendet.
  Über `DialResult.Call.TerminationReason` bleiben gleichzeitig die rohen
  Statuscodes sowie `Busy`, `Rejected` und `Failed` verfügbar. Der Client
  blieb registriert und war unmittelbar für einen erfolgreichen Folgeanruf
  wiederverwendbar.
- Eine caller-seitige Cancellation wird prompt als `DialStatus.Canceled`
  zurückgegeben. `DialResult.TerminationReason` liefert `Canceled` mit SIP
  487, Asterisk sieht den Wire-`CANCEL`, der Channel wird entfernt und dieselbe
  Registrierung bleibt für einen erfolgreichen Folgeanruf verwendbar.
- Ein Remote-BYE wird über `ICall.State` und `ICall.TerminationReason` ohne
  internen Zugriff sichtbar: `Completed`, kein künstlicher SIP-Status und
  `TerminatedBy.Remote`. Asterisk hatte keinen aktiven Channel mehr und
  dieselbe Registrierung trug den RTP-führenden Folgeanruf.
- Beim PBX-Ausfall wechselte `IPhoneLine.State` nach rund 5,1 Sekunden aus
  `Registered`. Nach dem Neustart stellte der eingebaute Re-Register-Loop den
  Asterisk-Contact automatisch in rund 1,4 Sekunden wieder her; der Folgeanruf
  führte RTP. Lebensdauer, Refresh und Backoff sind öffentlich typisiert
  konfigurierbar.
PR
[#105](https://github.com/BechsteinDigital/callora-voip-sdk/pull/105)
ist inzwischen gemergt und im neuen Main-Stand validiert. Der gemeinsame
Reason-Vertrag bestand für Callora mit 486/403/404; der kanonische
Asterisk-Block bestand zusätzlich mit lokalem und Remote-BYE. Die zuvor
festgestellte Granularitätslücke ist für diese Pfade damit geschlossen. Die
Follow-ups zu PR #107 und PR #114 schließen außerdem die zuvor reale
Cancellation-Cleanup-Lücke im geprüften Ringing-Pfad.

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
  `CallError` und Reason bereit; der Vergleichsadapter nutzt den Statuscode
  jetzt für denselben 486/403/404-Kategorievertrag.
- Bei caller-seitiger Cancellation löste der Adapter `HangUp()` aus. Asterisk
  sah das SIP-`CANCEL`, räumte den Channel auf und der Folgeanruf funktionierte.
- Der öffentliche `IPhoneCall.CallState` folgte dem Remote-BYE bis zum
  terminalen Zustand; Channel-Cleanup, Registrierung und Folgeanruf blieben
  intakt.
- Nach dem PBX-Neustart wurde der Asterisk-Contact automatisch in rund
  3,4 Sekunden neu aufgebaut und der Folgeanruf funktionierte. Der öffentliche
  `RegState` machte den vorherigen 20-sekündigen Ausfall jedoch nicht sichtbar,
  sondern blieb auf `RegistrationSucceeded`.
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
  `ClientCallFailed` liefert zusätzlich die SIP-Response; der Adapter nutzt
  sie jetzt für denselben 486/403/404-Kategorievertrag.
- Bei caller-seitiger Cancellation muss die Anwendung `Cancel()`, das
  Beobachten des laufenden Call-Tasks sowie MediaSession- und User-Agent-
  Cleanup selbst orchestrieren. Damit waren SIP-`CANCEL`, Channel-Cleanup und
  der Folgeanruf erfolgreich.
- `SIPUserAgent.IsCallActive` wechselte nach dem Remote-BYE auf `false`;
  Asterisk-Cleanup, Registrierung und Folgeanruf waren erfolgreich.
- `SIPRegistrationUserAgent.IsRegistered` machte den PBX-Ausfall nach rund
  15,0 Sekunden sichtbar. Der Contact wurde nach dem Neustart automatisch in
  rund 1,6 Sekunden wiederhergestellt; der Folgeanruf führte RTP.
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
Registrierung. Der neue Reason-Vertrag belegt zusätzlich, dass alle drei die
rohen Statuscodes liefern können. Callora kombiniert `DialStatus.Failed` mit
`DialResult.Call.TerminationReason` und bietet damit sowohl den einfachen
Managed Workflow als auch typisierte Retry-/Routing-Entscheidungen.

Beim aktiven Abbruch herrscht im geprüften Ringing-Pfad jetzt funktionale
Parität: Alle drei melden `Canceled`, senden `CANCEL`, beenden den
Remote-Channel und bleiben wiederverwendbar. Callora stellt darüber hinaus
den typisierten 487-Reason im bestehenden Managed Ergebnis bereit; Ozeki und
SIPSorcery benötigen weiterhin explizite Adapterlogik für ihren Cleanup.

Beim Remote-BYE herrscht dagegen funktionale Parität: Alle drei öffentlichen
Call-Modelle reflektieren die Peer-seitige Beendigung, räumen den externen
Channel auf und lassen die bestehende Registrierung weiterverwenden. Callora
benötigt dafür keine zusätzliche Lifecycle-Orchestrierung im Adapter und
klassifiziert den Vorgang zusätzlich als `Completed` durch den Remote-Peer.

Beim PBX-Neustart hat Callora den stärksten beobachtbaren Recovery-Vertrag:
Der typisierte Line-State zeigte den Ausfall am schnellsten, der öffentliche
Re-Register-Loop stellte den Contact ohne Anwendungsaktion wieder her und der
Folgeanruf funktionierte. SIPSorcery verhielt sich ebenfalls transparent,
erkannte den Ausfall mit den gemeinsamen Lease-Werten aber später. Ozeki
registrierte sich technisch wieder, ließ den Consumer während des
20-Sekunden-Ausfalls jedoch im erfolgreichen öffentlichen Zustand. Die
Nachvalidierung zeigte allerdings wechselnde Full-Run-Timeouts bei allen drei
Stacks, die im direkten 6/6-Wiederholungslauf verschwanden. Die
Recovery-Produkteigenschaften bleiben beobachtet; der Test selbst braucht
stabilere Wiederholungs- und Diagnosekriterien, bevor er ein hartes Gate ist.

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
lokalem Hold/Unhold, terminalen Remote-Ablehnungen und Peer-seitigem BYE;
Callora tut es hier mit der kleinsten funktionalen Integrationsfläche, während
Ozeki 10.5.1 den Abstand zur historischen Version sichtbar verkleinert. Die
neuen Slices stärken das Progressive-API-Argument: Calloras Managed Dial und
tieferes `ICall`-Verhalten lassen sich ohne Abstraktionsbruch kombinieren. Sie
zeigen zugleich, dass detaillierte Remote-Fehlerursachen inzwischen auf
demselben Call-Objekt erreichbar sind und ein aktiv abgebrochener klingelnder
Wahlversuch extern sauber terminiert. Noch nicht bewiesen ist eine generelle
Überlegenheit der übrigen Escape Hatches: Transfer, In-Dialog-SIP,
Custom-Header, Telemetrie, eigene Devices, Module, ICE und WebRTC wurden in
diesem Dreiervergleich nicht systematisch gegenübergestellt.

## Konkreter Callora-Handlungsbedarf

Aus der vollständigen Fünferreihe und der Nachvalidierung folgt keine weitere
Callora-Produktkorrektur für die geprüften Verträge. Verbleibend sind
Test-/Dokumentationsaufgaben:

1. Der neue kanonische L4-Cancellation-Test und der strengere
   Dreiervergleich müssen als Regression erhalten bleiben: sichtbares
   Ringing, `Canceled`/487, Wire-`CANCEL`, null aktive Channels und
   erfolgreicher Folgeanruf.
2. Der PBX-Restart-/Hold-Block sollte als wiederholbarer Benchmark stabilisiert
   werden: mehrere Versuche, Zeitreihen für Contact/State, eindeutige
   Cleanup-Diagnostik und getrennte Setup-/Recovery-Budgets statt eines
   einzigen harten Fensters.
3. README und Portal-Dokumentation sollten den bewiesenen Progressive-API-Pfad
   zeigen: Managed Dial plus `ICall` für Hold/Remote-BYE sowie
   `ICall.TerminationReason` für 486/403/404,
   `DialResult.TerminationReason` für Cancellation/487,
   `IPhoneLine.State`, `LineReconnecting`, `RegistrationExpiry` und
   `ReregisterOptions` für kontrollierbare Recovery.

Für Hold/Unhold, Remote-Ablehnung samt Reason, Remote-BYE und automatische
PBX-Recovery sowie Caller-Cancellation wurde in diesem Slice kein weiterer
Callora-Produktfix sichtbar.
