# Callora vs. Ozeki vs. SIPSorcery – gemeinsamer Asterisk-Slice

Dieses manuell gestartete Testprojekt enthält den ausführbaren Vergleich der
drei Stacks. Alle laufen gegen dieselbe programmgesteuert erzeugte
Asterisk-22-Konfiguration und erfüllen denselben beobachtbaren Vertrag.

Das Projekt ist absichtlich nicht Teil der Solution oder der normalen CI: Die
proprietären Ozeki-Pakete werden nicht im Repository verteilt. Der Runner nutzt
entweder eine lokale Ozeki-Installation oder extrahiert sie aus einem vom
Anwender bereitgestellten `.deb`.

Die Ozeki-Spalte verwendet `Ozeki.SDK.Linux` 10.5.1 aus dem offiziellen
.NET-10-Linux-Paket
`installlinux_1783492293_Ozeki-SDK-net10-v10.5.1.deb`. Das frühere Ergebnis
mit der historischen OzekiSDK 1.8.23.0 bleibt in [RESULTS.md](RESULTS.md) als
Vergleichswert dokumentiert.

## Voraussetzungen

- Linux mit glibc
- .NET SDK 10
- laufender Docker-Daemon
- `ar`, `awk`, `tar`, `zstd`, `sha256sum` und `gcc`
- lokaler Checkout der Callora SDK
- das bereitgestellte Ozeki-10.5.1-`.deb` oder eine Installation unter
  `/opt/ozekisdk`

Standardmäßig werden diese Pfade verwendet:

- Callora: Repository-Wurzel relativ zu diesem Testprojekt
- Ozeki-Paket:
  `~/Downloads/installlinux_1783492293_Ozeki-SDK-net10-v10.5.1.deb`

Der normale Lauf aus diesem Ordner benötigt keine systemweite Installation:

```bash
./run-interop.sh
```

An anderen Orten können die Pfade explizit gesetzt werden:

```bash
OZEKI_DEB_PATH=/absoluter/pfad/zum/Ozeki-SDK-net10-v10.5.1.deb \
./run-interop.sh \
  -p:CalloraSdkRoot=/absoluter/pfad/zur/voip-sdk
```

Der Runner extrahiert ausschließlich den NuGet-Ordner aus dem `.deb` in einen
SHA-256-adressierten Cache unter `/tmp` und startet danach `dotnet test`. Die
Ozeki-Pakete werden nicht in diesen Vergleich kopiert oder mit ihm verteilt.

## Verbleibende Ozeki-Linux-Kompatibilitätsschicht

Auch `Ozeki.SDK.Linux` 10.5.1 versucht beim ersten Runtime-Start, das feste
Verzeichnis
`/usr/share/Ozeki.{20d04fe0-3aea-1069-a2d8-08002b30309d}` anzulegen. Als
normaler Benutzer endet ein direkter Start mit `UnauthorizedAccessException`.
Das `.deb` enthält keinen `postinst`-Schritt, der diesen Pfad vorbereitet.

Der Runner kompiliert deshalb weiterhin einen engen `LD_PRELOAD`-Shim. Er
leitet ausschließlich diesen exakten Ozeki-Pfad in das temporäre
Runtime-Verzeichnis um und verändert weder SIP- noch RTP-Daten. Die
Kompatibilitätsschicht ist als eigener Aufwand in den Ergebnissen ausgewiesen.

## Szenarien

Jedes Szenario wird einmal mit Callora, Ozeki und SIPSorcery ausgeführt:

1. Digest-Registrierung
2. Outbound-Call und RTP-Empfang
3. Inbound-Call, Answer und RTP-Empfang
4. No-Answer mit vier Sekunden Connect-Timeout
5. RFC-4733-DTMF `1234`
6. PCM16-WAV-Playback über einen Asterisk-Echo-Call
7. Aufnahme eingehender PCMU-Medien als PCM16-WAV
8. Medien-Bridge von einem Milliwatt-Quell-Leg zu einem Echo-Leg
9. Hold/Unhold per re-INVITE mit öffentlichem Zustandswechsel,
   Asterisk-sichtbarem `sendonly`/`inactive` → `sendrecv` und fortgesetztem RTP
10. Remote-Ablehnung mit 486, 403 und 404: prompter Fehler,
    Channel-Cleanup und erfolgreicher Folgeanruf über dieselbe Registrierung
11. Caller-seitige Cancellation während des Klingelns: normalisierter
    Abbruchstatus, SIP-`CANCEL` und Channel-Cleanup sowie erfolgreicher
    Folgeanruf über dieselbe Registrierung
12. Remote-BYE nach aufgebautem Gespräch: öffentlicher Zustandswechsel,
    Channel-Cleanup und erfolgreicher Folgeanruf über dieselbe Registrierung
13. PBX-Neustart: Registrierungsverlust, automatische Wiederanmeldung,
    wiederhergestellter Asterisk-Contact und erfolgreicher RTP-Folgeanruf
14. Hangup, Channel-Cleanup und Deregistrierung

Für jede Testzeile wird ein frischer Asterisk-Container gestartet. Die Tests
laufen absichtlich seriell. Dadurch teilen sich die Implementierungen weder
Registrierungskontakte noch Dialog- oder Medienzustand.

Der Cancellation-Test ist eine Charakterisierungsmatrix und kein künstlich
grüner Gleichstand: Der erwartete Unterschied beim `CANCEL` und externen
Channel-Cleanup ist als Capability pro Stack hinterlegt. Dadurch bleibt der
aktuelle Callora-Nachteil sichtbar und eine spätere Verhaltensänderung fällt
im Test auf.

## Fairness des Vergleichs

- Alle Stacks verhandeln ausschließlich Plain RTP mit PCMU/8 kHz.
- Asterisk, Credentials, Dialplan, Timeouts und Assertions sind identisch.
- Der PBX-Restart nutzt für alle Stacks eine öffentlich konfigurierte
  Registrierungslebensdauer von zehn Sekunden. Asterisk akzeptiert fünf bis
  120 Sekunden; der Ausfall bleibt höchstens 20 Sekunden unbeobachtet.
- Callora nutzt seine öffentlichen High-Level-APIs für Playback, Recording und
  `MediaConnector.CrossConnect`; Hold/Unhold läuft direkt über `ICall`.
- Ozeki nutzt seine öffentlichen Softphone-, Call- und Medienbausteine wie
  `PhoneCallAudioReceiver`, `PhoneCallAudioSender`, `MediaConnector`,
  `WaveStreamPlayback` und `WaveStreamRecorder`. Anders als bei der alten DLL
  funktioniert WAV-Playback in 10.5.1 direkt über den nativen Medienpfad.
- SIPSorcery nutzt seine öffentlichen SIP-/Media-APIs. Da das Basispaket keine
  produktfertige PCMU-WAV-Pipeline bereitstellt, gehören Audioquelle/-senke,
  G.711-µ-law-Konvertierung, WAV-I/O, Recording und Bridging sichtbar zum
  SIPSorcery-Adapter.
- Gemeinsamer Vertrag, Asterisk-Fixture, Tondatei-Erzeugung und Assertions
  werden keinem Stack zugerechnet.
- Ozekis Paketbootstrap und Linux-Pfadshim werden getrennt von der
  funktionalen C#-Adapterfläche ausgewiesen.

Der Szenarienzuschnitt stammt aus dem alten Dialer-/Callora-Slice und wurde
ursprünglich anhand der Callora SDK formuliert. Die externen
Asterisk-Beobachtungen sind für alle gleich; die Codeflächenmetrik ist dadurch
aber kein vollständig herstellerneutraler Ergonomie-Benchmark. Sie misst
bewusst, wie gut jeder Stack zu genau diesem Produktvertrag passt.

## Calloras progressive API im Vergleich

Der Callora-Adapter prüft nicht ausschließlich einen gekapselten Happy Path.
Der Slice nutzt zwei Tiefen derselben öffentlichen API:

- Managed Workflows für Registrierung, Dial-and-wait, Playback und Recording.
- Typisierte bzw. mediennahe Verträge über `ICall`, ausgehandelte
  Medienparameter, per-call `IMediaReceiver`/`IMediaSender` und
  `MediaConnector.CrossConnect`.

Damit ist praktisch belegt, dass Calloras Komfortschicht den Zugriff auf
Call-Zustand und Medienpfad nicht abschneidet: Ein Dialer kann mit wenig Code
starten und für einzelne Anforderungen tiefer gehen, ohne die SDK zu ersetzen
oder interne Typen anzusprechen. Der zusätzliche Hold-Slice belegt dieses
Muster jetzt auch für tiefergehende Call-Steuerung: Managed Dial, typisiertes
`ICall.HoldAsync`/`UnholdAsync`, beobachtbarer Zustand und derselbe
Asterisk-Medienpfad greifen ohne internen API-Zugriff ineinander. Der
Remote-BYE-Slice nutzt denselben öffentlichen Call-Zustand, um ein vom Peer
beendetes Gespräch ohne stack-spezifischen internen Zugriff zu erkennen. Beim
PBX-Neustart werden außerdem `IPhoneLine.State`, `SipAccount.RegistrationExpiry`
und `ReregisterOptions` direkt genutzt; die Komfortregistrierung bleibt damit
bis zur steuerbaren Recovery-Oberfläche durchlässig.

Nicht vergleichend geprüft wurden die übrigen öffentlichen Escape Hatches wie
Transfer, In-Dialog-`INFO`/`OPTIONS`/`SUBSCRIBE`/`NOTIFY`, Custom-Header,
Quality-/ICE-Events, eigene Audio-Devices, Telemetrie-Sinks und Module. Der
aktuelle Lauf belegt daher Calloras abgestufte API im verwendeten Ausschnitt,
aber keine generelle Überlegenheit dieser tieferen Oberfläche gegenüber Ozeki
oder SIPSorcery.

## Was der Slice nicht beweist

Das ist ein Funktions- und Integrationsvergleich, kein Last-, Qualitäts-,
Lizenz- oder Kostenbenchmark. Nicht abgedeckt sind insbesondere:

- TLS, SRTP, ICE/NAT-Traversal und wechselnde Netzwerke
- andere Codecs als PCMU
- Transcoding-Qualität und akustische Qualitätsmetriken
- Parallelität mit vielen Calls, Turbo-Dialing und Race Conditions
- Transfer, Remote-Hold/Glare, Fax, Konferenzmischung und In-Band-DTMF
- Transportabbruch während aktiver Calls und wiederholte Fehlerstürme
- Granularität der öffentlich sichtbaren SIP-Fehlerursache; der gemeinsame
  Vertrag normalisiert 486, 403 und 404 bewusst zu `Failed`
- Authentifizierung, Mandantentrennung und die REST-Oberfläche des Dialers
- fachliche Gleichwertigkeit aller 77 WebMethods des alten ASMX-Dialers
- Supportqualität, Lizenzkosten oder Produktionsfreigabe
- Kamera-, physische Audio- und alle weiteren nativen Funktionen des
  kombinierten Ozeki-Pakets
- einen vollständigen Vergleich aller tieferen Call-, Media- und
  Erweiterungspunkte der drei SDKs

Das `.deb` deklariert zusätzliche native Abhängigkeiten für Kamera und
Audio-Hardware. Der hier geprüfte headless SIP-/RTP-Slice hat sie nicht
benötigt; daraus folgt keine Aussage über andere Ozeki-Funktionen.

Die Bridge-API ist auf allen Seiten bidirektional verdrahtet; der Test weist
gezielt die Richtung Quell-Leg → Echo-Leg nach, ohne eine künstliche
Echo-Schleife zwischen zwei Echo-Applikationen zu erzeugen.

Als Callora-Basis gilt der oben genannte Main-Commit. Der noch offene PR #105
zur detaillierteren Beendigungsursache gehört nicht zu diesem Messstand. Er
adressiert außerdem nicht den separat beobachteten Cleanup bei
caller-seitiger Cancellation.

Die gemessenen Ergebnisse stehen in [RESULTS.md](RESULTS.md).
