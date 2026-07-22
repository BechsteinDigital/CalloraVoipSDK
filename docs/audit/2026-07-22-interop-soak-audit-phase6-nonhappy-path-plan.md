# Phase 6 — Non-Happy-Path L4-Interop gegen Asterisk

> Teil des Interop+Soak+Audit-Pakets. **Nur Test-Infrastruktur + Doku — keine SDK-Verhaltensänderung.**
> Ausnahme: `InternalsVisibleTo`-Test-Freischaltung (gleiche Kategorie wie F001, kein Fix).
> Scope vom User bestätigt: **A + B + F005-Vertiefung**.

## Ergebnis (2026-07-22)

Umgesetzt: **Gruppe A (REGISTER-Non-Happy-Path)** — 3 grüne Tests gegen echten Asterisk + 1 Skip.
Die empirische Verifikation korrigierte zwei Plan-Annahmen (die Recon war ungenau — daher „erst messen, dann festschreiben"):

- **F005 (verifiziert):** Auth-Ablehnung (falsches PW / unbekannter User) wird NICHT als `Failed` gemeldet, sondern als `Timeout` mit `Error == null` nach vollem Timeout — obwohl die Line intern `LineState.Failed` erreicht. Kein Short-Circuit. (Register F005.)
- **F006 (root-caused → schwerer Interop-Fund, blockiert Gruppe B):** **Authentifizierte ausgehende Calls an Asterisk scheitern grundsätzlich** mit `481 Call/Transaction Does Not Exist` am **Auth-Retry-INVITE** (CSeq 2 nach 401-Challenge) — uniform über alle Ziele, unabhängig von Transport und SRTP. **Differenzierer: REGISTER-Auth-Retry geht, INVITE-Auth-Retry nicht** → Kandidat: To-Tag-Echo aus der 401 in den re-INVITE (Asterisk hält ihn für in-dialog). Der zuvor beobachtete `Connection refused` war ein **Nebenbefund** (SDK eskaliert den 1305-B-INVITE RFC 3261 §18.1.1-konform auf TCP; die UDP-only-Fixture hatte keinen TCP-Transport). Damit ist auch die **F005-Taxonomie-Prämisse** (Codes aus `.Error`) empirisch nicht haltbar — Calls erreichen den Dialplan nie. (Register F006.)

**Gruppe B, Fixture-Dialplan (`extensions.conf`) und die `InternalsVisibleTo`-Freischaltung wurden zurückgestellt**, bis F006 geklärt ist — sonst käme ungenutzte Infrastruktur / nie laufende Skip-Tests in den PR. **Nächste Schritte für F006:** (1) volles Wire-Logging des Auth-Retry-INVITE → To-Tag-Echo bestätigen/widerlegen; (2) gegen zweiten Peer (FreeSWITCH/Fritzbox) prüfen → SDK-Defekt vs. Asterisk-spezifisch. Repro: `[transport-tcp]` in pjsip.conf (oder `SrtpPolicy.Disabled`) + Dialplan `[default]` (busy→Busy(), decline→Hangup(21), noanswer→Ringing()+Wait).

Der folgende ursprüngliche Plan bleibt als Referenz für die Fortsetzung (Gruppe B) erhalten.

## Ziel

Den bisher einzigen grünen Interop-Durchstich (Asterisk-REGISTER-Happy-Path) um systematisches
**Non-Happy-Path** erweitern: Auth-Fehler, Call-Ablehnung, CANCEL, Timeout — über die öffentliche
Facade gegen einen echten Asterisk. Ergebnis: neue/gehärtete Audit-Register-Einträge, v. a. **F005**.

## Ebene & Rahmen

- **L4-Facade** (`VoipClient.ConnectAsync` / `DialAndWaitUntilConnectedAsync`) gegen echten Asterisk
  (Testcontainers, `andrius/asterisk:22`), `[Trait("Category","Interop")]`, `[DockerRequiredFact]`.
- Läuft nur im Interop-CI-Job / nightly, **nicht** im PR-CI (bereits so gegated).
- **Split+Skip-Policy:** deckt ein Test einen echten SDK-Defekt auf → grüner Teil bleibt hart,
  defekt-belegende Assertion wird `[Fact(Skip="Fxxx …")]`.

## Facade-Taxonomie (aus Recon, Fundstellen)

- `ConnectResult{ Status: ConnectStatus{Registered,Timeout,Canceled,Failed}, FinalLineState, Error }`
  — `Error` (public) trägt bei 401/403 die **interne** `SipRegistrationFailedException.StatusCode`.
- `DialResult{ Status: DialStatus{Connected,Timeout,Canceled,Failed}, Error }`
  — `Error` (public) trägt bei 486/603/404 die **interne** `SipFinalResponseException`
  (`FinalResponse.Response.StatusCode`).
- `ICall` exponiert den SIP-Code **nicht** (nur `CallState.Terminated`).

## Aufgabe 1 — Fixture: extensions.conf + IVT-Freischaltung

**Dateien:**
- Ändern: `tests/CalloraVoipSdk.InteropTests/Asterisk/AsteriskContainer.cs`
- Ändern: `src/Core/Properties/AssemblyInfo.cs` (IVT-Eintrag für InteropTests — Test-Freischaltung, kein Fix)

**AsteriskContainer:** zusätzliche `extensions.conf` mounten (`/etc/asterisk/extensions.conf`), gleiche
Temp-Datei-Technik wie pjsip.conf. Dialplan-Kontext `[default]` mit Test-Extensions. Kandidaten
(gegen echten Container zu verifizieren — App→SIP-Mapping live prüfen):
```
[default]
exten => busy,1,Busy()            ; erwartet SIP 486 Busy Here
exten => decline,1,Hangup(21)     ; Q.850 cause 21 → erwartet SIP 603 Decline
exten => noanswer,1,Ringing()
exten => noanswer,2,Wait(3600)    ; ringt „ewig" → SDK-seitiger ConnectTimeout → DialStatus.Timeout
; unbekannte Extension → Asterisk 404 (ohne Eintrag)
```
Neue Accessor evtl.: Ziel-URIs bauen (`sip:busy@<ip>`, `sip:decline@<ip>`, `sip:noanswer@<ip>`,
`sip:nonexistent@<ip>`). REGISTER-Endpoint 6001/secret bleibt.

**IVT:** `[assembly: InternalsVisibleTo("CalloraVoipSdk.InteropTests")]` in Core-AssemblyInfo, damit
die F005-Vertiefung auf die internen Exception-Typen casten kann. Dokumentiert unter F001-Kategorie.

## Aufgabe 2 — Gruppe A: REGISTER-Non-Happy-Path (keine Config-Änderung)

**Datei:** `tests/CalloraVoipSdk.InteropTests/Registration/AsteriskRegisterFailureInteropTests.cs`

| Test | Setup | Erwartung (L4) |
|---|---|---|
| Falsches Passwort | 6001 + „wrong" | `ConnectStatus.Failed`, `FinalLineState` RegistrationFailed/Failed |
| Unbekannter User | 9999 + irgendein PW | `ConnectStatus.Failed` |
| Server unerreichbar | tote IP/Port, kurzer `ConnectOptions.Timeout` | `ConnectStatus.Timeout` |

## Aufgabe 3 — Gruppe B: INVITE/Call-Non-Happy-Path

**Datei:** `tests/CalloraVoipSdk.InteropTests/Calls/AsteriskCallFailureInteropTests.cs`
Vorbedingung je Test: 6001 registrieren (Happy-Path-REGISTER), dann `DialAndWaitUntilConnectedAsync`.

| Test | Ziel | Erwartung (L4) | Config |
|---|---|---|---|
| Unroutbar/404 | `sip:nonexistent@ip` | `DialStatus.Failed` | keine |
| CANCEL vor Antwort | ringendes Ziel + extern gecancelter Token | `DialStatus.Canceled` | `noanswer` |
| 486 Busy | `sip:busy@ip` | `DialStatus.Failed` | Busy() |
| 603 Decline | `sip:decline@ip` | `DialStatus.Failed` | Hangup(21) |
| No-Answer-Timeout | `sip:noanswer@ip`, kurzer ConnectTimeout | `DialStatus.Timeout` | Ringing+Wait |

## Aufgabe 4 — F005-Vertiefung + Register

**Datei:** `tests/CalloraVoipSdk.InteropTests/Calls/FacadeFailureTaxonomyInteropTests.cs`

Belegt beide Seiten der Lücke:
1. **Kollaps:** 486 und 603 liefern **denselben** `DialStatus.Failed`; falsches-PW und unbekannter-User
   liefern **denselben** `ConnectStatus.Failed` → Aufrufer kann sie am Status nicht unterscheiden.
2. **Rückgewinnbar:** `result.Error` (bzw. `.InnerException`) ist die interne
   `SipFinalResponseException`/`SipRegistrationFailedException` mit **distinktem** `StatusCode`
   (486 ≠ 603; 401/403) → der Code EXISTIERT, die Facade surft ihn nur nicht als First-Class-Feld.

**Register-Eintrag F005** (Typ Facade-Coupling-Gap / DX, Info): Facade kollabiert die SIP-Fehler-
Taxonomie; Fix-Vorschlag = ein öffentliches `FailureReason`/`SipStatusCode`-Feld an
`ConnectResult`/`DialResult` (der Wert liegt bereits in `.Error` vor). Kein autonomer Fix.

## Validierung

- Lokal gegen echten Asterisk (Docker vorhanden): jeden Dialplan-App→SIP-Mapping live verifizieren,
  bevor die Erwartung festgeschrieben wird (nicht raten).
- Alle neuen Tests `[Trait("Category","Interop")]` → nicht im PR-CI; Interop-Job führt sie aus.
- Bestehender REGISTER-Happy-Path + Smoke bleiben grün.
- Neue echte Fehlverhalten (hängende States, fehlendes Cleanup) → als weitere Findings ins Register,
  Split+Skip wo nötig.

## Reihenfolge

1. Aufgabe 1 (Fixture + IVT) — Basis.
2. Aufgabe 2 (Gruppe A) — schnell, kein Dialplan.
3. Aufgabe 3 (Gruppe B) — Dialplan live iterieren.
4. Aufgabe 4 (F005-Vertiefung + Register).
5. Commit je Aufgabe; Merge/Push am Paketende nach Review.
