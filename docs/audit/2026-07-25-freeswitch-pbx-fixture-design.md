# Spec: FreeSWITCH als zweite `IPbxFixture`-Implementierung (Interop Phase B.2)

**Status:** Freigegeben (User 2026-07-25) · **Datum:** 2026-07-25 · **Branch:** neuer Branch off B.1/main · **Teil von:** Interop-+Soak-+Audit-Paket, Phase B (Multi-PBX) · **Vorgänger:** B.1 (`IPbxFixture`-Abstraktion, `docs/audit/2026-07-25-pbx-fixture-abstraction-design.md`)

## 1. Kontext & Ziel

B.1 hat die Asterisk-Zwei-Bein-Media-Szenario-Matrix hinter das schmale `IPbxFixture`-Interface gestellt (abstrakte Basisklassen `TwoLegMediaMatrix`/`TwoLegDtmfMatrix`/`TwoLegHoldMatrix`/`TwoLegTransferMatrix`/`ConcurrentCallSoakMatrix` + Asterisk-Subklassen). **B.2-Ziel:** eine zweite `IPbxFixture`-Implementierung gegen **FreeSWITCH** bauen, sodass dieselbe Matrix „gratis" gegen einen zweiten Fremd-Stack läuft — der Beweis, dass die Abstraktion trägt.

**Machbarkeit vorab bewiesen (Spike 2026-07-25):** `safarov/freeswitch:latest` ist öffentlich pullbar (kein Auth), FreeSWITCH bootet, Sofia-`internal`-Profil bindet 5060 (UDP+TCP), über die Container-Bridge-IP erreichbar — **gleicher Zugriffspfad wie Asterisk** (`ContainerIpAddress:5060`, Linux-only, CI-Linux+lokal). fs_cli/Event-Socket ist im Vanilla-Image kaputt (IPv6-`::`-Bind), wird für die Media-Matrix aber nicht gebraucht (Bridge läuft rein über SIP+Dialplan). Signalwire-Cert-Fehler ist harmlos.

**Nicht-Ziel (B.2):** Änderung des `IPbxFixture`-Interfaces oder der Matrix-Basisklassen (die sind fix aus B.1); Inbound-Originate/fs_cli-Steuerung; 3CX/Fritzbox; sofortige CI-Aufnahme (lokal-first, s. §7). Keine SDK-`src/`-Änderung.

## 2. Was die Matrix von der PBX braucht (aus B.1)

`IPbxFixture`: `StartAsync`, `SipHost`, `SipUdpPort`, `BridgePair(PbxMediaMode mode, int index)` → `PbxBridgePair(Caller, Callee, BridgeDialUri)`, `MediaPlaybackUri`, `GetLogsAsync`. Konkret: registrierbare Endpunkt-Paare (Plain + SDES + N Soak-Paare), ein Bridge-Dial-Target (Caller wählt Extension → PBX brückt an registrierten Callee), eine Media-Playback-Extension (Endlos-Ton, für die Attended-Transfer-Konsultation), Diagnose-Logs.

## 3. Ansatz: Config-Overlay

`safarov/freeswitch:latest` bootet mit vollständiger Vanilla-Config. **Wir überlagern nur das Nötige** und lassen den bootenden Rest (Modul-Loading, `internal`-Sofia-Profil, `vars.xml`) unangetastet:

- **Directory** (`/etc/freeswitch/directory/default/`): unsere User-XML (Plain-Paar, SDES-Paar, Soak-Paare) ersetzen bzw. ergänzen die Vanilla-Default-User (1000–1019).
- **Dialplan** (`/etc/freeswitch/dialplan/default.xml`): unser Bridge-/Media-Dialplan ersetzt den Vanilla-Default.
- Sonst nichts. Signalwire-Modul-Fehler wird ignoriert (kein Funktionsbezug).

`// DECISION:` Overlay statt Minimal-Clean-Config — kleinste Änderungsfläche, nutzt die schon bewiesene Boot-Config; FreeSWITCHs Minimal-Config (modules/sofia/vars) wäre deutlich aufwändiger und riskanter.

## 4. Architektur & Komponenten

Spiegelt die Asterisk-Seite; **`AsteriskContainer` bleibt unangetastet** (Vorbild, nicht geteilt — beide sind eigenständige Container-Fixtures).

- **`FreeSwitchContainer`** (neu, `tests/CalloraVoipSdk.InteropTests/FreeSwitch/`): baut `safarov/freeswitch:latest` via `ContainerBuilder`, mountet generierte Directory- + Dialplan-XML via `WithResourceMapping(FileInfo, FileInfo)` (reguläre Temp-Dateien, wie Asterisk — die Byte-Array-Variante wird von FreeSWITCH ignoriert). **Log-basierte Wait-Strategy** auf eine Sofia-internal-ready-Zeile (empirisch im Spike-Log zu bestimmen, z. B. die `internal`-Profil-Started-Meldung) statt des defekten Container-Healthchecks. Ctor nimmt `bridgePairs`-Count, generiert Soak-Paare (analog `AsteriskContainer.BuildSoakPjsipConf`). Exponiert `ContainerIpAddress`, `StartAsync`, `GetConsoleLogsAsync`, Endpoint-Accessoren, `CallTargetUri`, `IAsyncDisposable`.
- **`FreeSwitchPbxFixture : IPbxFixture`** (neu, `tests/CalloraVoipSdk.InteropTests/Pbx/`): dünnes Mapping auf `FreeSwitchContainer` — `SipHost`→`ContainerIpAddress`, `SipUdpPort`→5060, `BridgePair(Plain,0)`/`(Sdes,0)`/`(Plain,i>0)`→User-Paare, `MediaPlaybackUri`→Media-Extension-URI, `GetLogsAsync`→`GetConsoleLogsAsync`. Ctor `FreeSwitchPbxFixture(int bridgePairs = 1)` reicht den Count durch. Struktur identisch zu `AsteriskPbxFixture`.
- **5 `FreeSwitch…Matrix`-Subklassen** (Einzeiler), je `protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);`, getaggt `[Trait("Category", "InteropFreeSwitch")]`. Basisklassen aus B.1 unverändert.

## 5. Config-Mappings (Asterisk → FreeSWITCH)

| Asterisk (PJSIP) | FreeSWITCH |
|---|---|
| Endpoint + `type=auth`/`userpass` | Directory-User-XML: `<user id="6001"><params><param name="password" value="secret"/></params>…</user>`; Domain = Container-IP (Vanilla-`$${domain}`), SDK registriert `6001@<bridge-ip>` |
| `allow=ulaw` (Codec-Pin) | User-Var `absolute_codec_string=PCMU` (bzw. mehrere Codecs für den Mismatch-Fall) |
| `media_encryption=sdes` | SRTP-SDES per User-Var `rtp_secure_media=mandatory` (SDES-Callee); **per-Feature measure-first validiert** |
| `Dial(PJSIP/6003)` | Dialplan `<extension name="bridge-6003"><condition field="destination_number" expression="^6003$"><action application="bridge" data="user/6003@$${domain}"/></condition></extension>` (B2BUA → **immer im Medienpfad**, kein `direct_media`, kein `bypass_media`) |
| `Milliwatt()` | Media-Extension: `<action application="answer"/><action application="playback" data="tone_stream://%(0,0,1004)"/>` (endloser 1004-Hz-Ton) |
| Wait `"Asterisk Ready."` | log-Wait auf Sofia-internal-ready-Zeile (im Slice 1 empirisch fixiert) |

**B2BUA-Konsequenz:** FreeSWITCH terminiert jedes Bein und ist damit inhärent im Medienpfad — das Asterisk-`direct_media=no`-Problem entfällt. Dialplan darf `bypass_media` nicht setzen.

## 6. Slice-Plan (inkrementell, measure-first)

Jede Fähigkeit wird gegen echtes FreeSWITCH gemessen, bevor Assertions festgeschrieben werden (Recon ist bei Interop oft falsch). Die `FreeSwitch…Matrix`-Subklassen sind trivial; das Risiko steckt ganz im `FreeSwitchContainer`-XML.

1. **`FreeSwitchContainer` + Register-Smoke:** Directory (Plain-Paar) + Bridge-Dialplan + log-Wait; ein Smoke-Test registriert den Caller über `FreeSwitchPbxFixture` an echtes FreeSWITCH (analog `AsteriskPbxFixtureTests`).
2. **Plain-Media-Matrix:** `FreeSwitchPbxFixture` vervollständigen + `FreeSwitchTwoLegMediaMatrix`-Subklasse; Bridge-Aufbau + bidir. Paketzähler + byte-exakter Plain-Content grün. (Der SRTP-Content-Test erbt `InteropLocalMedia` aus der Basis → auch hier lokal-only.)
3. **DTMF / Hold / Transfer:** je Subklasse; Media-Playback-Extension für die Transfer-Konsultation; DTMF-Relay (RFC 4733), Hold-re-INVITE, REFER/Replaces per-Feature validiert.
4. **SDES:** SDES-Directory-User (`rtp_secure_media=mandatory`) + Wiring; SRTP-Content bleibt lokal (`InteropLocalMedia`).
5. **Soak:** Soak-Paar-Generierung im `FreeSwitchContainer` + `FreeSwitchConcurrentCallSoakMatrix`.
6. **Voll-Regression lokal:** alle `FreeSwitch…Matrix` grün gegen echtes FreeSWITCH; Asterisk-Suite + PR-CI unberührt; `git diff` gegen `src/` leer.

## 7. CI & Verhaltensbewahrung

- **Lokal-first:** die `FreeSwitch…Matrix`-Subklassen tragen `Category=InteropFreeSwitch`; der PR-CI-Interop-Filter wird auf `Category=Interop&Category!=InteropLocalMedia&Category!=InteropFreeSwitch` erweitert → FreeSWITCH läuft NICHT im PR-CI-Gate. Sobald lokal über mehrere Läufe stabil, in einem **Folge-Commit** ins Gate aufgenommen (dann zieht evtl. auch ein eigener FreeSWITCH-Image-Pull-Step in die CI ein).
- **Verhaltensbewahrend:** keine `IPbxFixture`-/Basisklassen-Änderung, keine `src/`-Änderung, `AsteriskContainer`/Asterisk-Subklassen unberührt. Die Asterisk-Matrix + die restliche Interop-Suite bleiben grün.
- **Fallback:** falls `safarov/freeswitch:latest` unerwartet bricht, Ausweich auf ein anderes öffentliches Image bzw. ein Minimal-Dockerfile (Ansatz (b) aus dem Brainstorming) — nur bei Bedarf.

## 8. Entscheidungen

- `// DECISION:` Image `safarov/freeswitch:latest` (Spike-bewiesen), Fallback nur bei Bedarf.
- `// DECISION:` Config-Overlay (Directory + Dialplan) statt Minimal-Clean-Config.
- `// DECISION:` Eigenständiger `FreeSwitchContainer` (Asterisk-Fixture adaptieren als Vorbild, nicht teilen) — hält beide Fixtures fokussiert und die Register-/Transport-Asterisk-Tests unberührt.
- `// DECISION:` Log-basierte Wait-Strategy statt des defekten Container-Healthchecks.
- `// DECISION:` `InteropFreeSwitch`-Kategorie, lokal-first, CI-Aufnahme als separater Folge-Schritt.
- `// DECISION:` fs_cli/Event-Socket + Inbound-Originate außen vor (Media-Matrix braucht sie nicht).
