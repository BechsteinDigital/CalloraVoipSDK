# IP-Herkunft, Abhängigkeiten und Lizenzlage

> **Teil des technischen Due-Diligence-Pakets.** Stand: 2026-07-27.
>
> Diese Seite dokumentiert für die Käufer-Due-Diligence, welcher Code Eigenentwicklung ist,
> welche Fremdkomponenten zur Laufzeit mitgeliefert werden und unter welcher Lizenz jede davon
> steht. Alle Aussagen sind gegen den Quellbaum (`src/`), die Projektdateien (`*.csproj`,
> `src/Directory.Build.props`), die `LICENSE`-Datei und `THIRD-PARTY-NOTICES.md` im Repo-Root
> verifiziert. Wo Prosa und Nachweis auseinanderlaufen, gilt der Nachweis (Prinzip „Doku ≤ Nachweis").
>
> Dieser Text ist eine technische Bestandsaufnahme für die Due Diligence, **keine Rechtsberatung**.
> Vor einem kommerziellen Asset-Kauf ist eine abschließende juristische Lizenz- und
> IP-Prüfung zu empfehlen (so auch der Hinweis am Ende von `THIRD-PARTY-NOTICES.md`).

---

## 1. Eigener Stack — Kernaussage

Der **Protokoll-Kern ist Eigenentwicklung**. Signaling-, Medien- und Medien-Sicherheits-Wire-Formate
sowie die zugehörigen State-Machines sind im Repository selbst implementiert; es gibt **keine
Laufzeitabhängigkeit auf einen externen SIP-Stack** (kein SipSorcery, kein pjsip, kein sofia-sip o. ä.
als NuGet-/Binär-Abhängigkeit).

Konkret liegen als Eigencode im Baum (jeweils eigenes Wire-Codec- und Parser-/State-Modul):

| Subsystem | Ort im Baum (Beleg) |
|---|---|
| SIP-Wire (Parser/Serializer, Requests/Responses) | `src/Core/Infrastructure/Sip/Wire/` (`ISipWireCodec`, `SipResponse`) |
| SIP-Signaling / Dialog- & Transaktions-State | `src/Core/Infrastructure/Sip/Signaling/`, `.../Adapters/` (`SipCallSignalingService`, `SipCoreCallChannel`) |
| SDP (Parser, Offer/Answer-Negotiator) | `src/Core/Infrastructure/Sdp/` (`ISdpSessionParser`, `SdpOfferAnswerNegotiator`, `SdpUtilities`) |
| RTP/RTCP-Wire + Session/Jitter/Packetisation | `src/Core/Infrastructure/Rtp/` (`IRtpPacketCodec`, `RtpPacketCodec`, `RtpSession`, `JitterBuffer`) |
| SRTP/SRTCP-Krypto-Kontext | `src/Core/Infrastructure/Srtp/` (`SrtpContext`, `AesCmCipher`) |
| STUN (Wire, Attribute, Client, Server, ICE) | `src/Core/Infrastructure/Stun/` (`IStunMessageCodec`, `StunMessage`) |
| TURN (Wire, Attribute, Client) | `src/Core/Infrastructure/Turn/` (`TurnAttributeMapper`, `ITurnClient`) |
| DTLS-SRTP-Anbindung (Handshake-Orchestrierung/Keying) | `src/Core/Infrastructure/Dtls/` (`DtlsSrtpHandshaker`, `DtlsSrtpKeyExporter`) — nutzt BouncyCastle als Krypto-Primitiv, siehe §3 |

### SipSorcery — nur Referenzmaterial, kein Code-Import

- **Kein Package-Reference** auf SipSorcery in irgendeiner `*.csproj` (verifiziert: die vollständige
  Liste der `PackageReference`-Einträge steht in §2 und enthält SipSorcery nicht).
- **Kein `using SipSorcery`** und kein SipSorcery-Namespace im Quellcode. Der einzige Treffer im
  gepflegten Quellbaum ist ein **erklärender Kommentar** in
  `src/Core/Domain/Events/OutboundCallRingingEventArgs.cs` (Zeile 8), der ein Verhalten mit
  SipSorcerys `ClientCallRinging` vergleicht — reine Doku-Referenz, kein Code. (Weitere Treffer bei
  einer naiven Textsuche liegen ausschließlich in Build-Artefakten unter `bin/`/`obj/` und stammen
  aus ebendiesem XML-Doku-Kommentar; kein Quellcode.)
- Strategische Vorgabe hierzu (verifiziert im internen Produkt-Memory, Abschnitt „Strategic
  Technology Direction", Z. 54–57; intern, nicht Teil des Pakets, auf Anfrage/NDA):
  - *„Final target: own SIP/RTP/SRTP stack (no runtime dependency on SipSorcery)."*
  - *„SipSorcery may be used as learning/reference material for protocol behavior."*
  - *„Do not do blind code copy; any reuse must respect licensing obligations."*

**Bewertung:** SipSorcery ist erklärte Lern-/Referenzquelle, keine Laufzeit- oder Build-Abhängigkeit.
Für die IP-Herkunft ist relevant, dass SipSorcery selbst unter **BSD-3-Clause** (permissiv) steht;
selbst wenn Muster daraus als Referenz gedient haben, entstünde daraus **keine Copyleft-Verpflichtung**.
Ein forensischer Code-Ähnlichkeits-Vergleich (Blind-Copy-Ausschluss) ist mit den vorliegenden
Artefakten nicht durchgeführt und bleibt Gegenstand der juristischen Prüfung — siehe §6, Hinweis (H1).

Querverweise: `../../adr/ADR-028-dtls-srtp-foundation.md` (DTLS/BouncyCastle),
`../../adr/ADR-049-opus-codec-integration-concentus.md` (Opus/Concentus),
`./architecture.md` (Schicht- und Modulkarte).

---

## 2. Reale Laufzeit-Abhängigkeiten (aus den `*.csproj`)

Vollständige Liste aller `PackageReference`-Einträge über `src/` (dedupliziert). „Kopplung" gibt an,
welches Auslieferungsprojekt die Abhängigkeit zieht: **Core** = `CalloraVoipSdk.Core` (Pflicht-Kern),
**Client** = `CalloraVoipSdk.Client` (Facade), **Audio.Linux/Windows** = optionale Audio-Adapter.

| Paket | Version | Lizenz | Zweck | Kopplung |
|---|---|---|---|---|
| BouncyCastle.Cryptography | 2.6.2 | MIT (bespoke MIT-style) [siehe H2] | DTLS-SRTP-Krypto-Primitive, SHA-512/256, ECDSA-P256-Zertifikate | Core |
| Concentus | 2.2.2 | BSD-3-Clause | Opus-Encode/Decode (reiner C#-Opus-Port) | Core |
| DnsClient (DnsClient.NET) | 1.8.0 | Apache-2.0 | DNS-SRV-Auflösung für SIP-Server-Discovery | Core |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | DI-Abstraktionen (kein Container-Zwang) | Core, Client |
| Microsoft.Extensions.Hosting.Abstractions | 8.0.1 | MIT | Hosting-/Lifecycle-Abstraktionen | Core, Client |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | Logging-Abstraktionen (kein Logger-Zwang) | Core, Client |
| Microsoft.Extensions.Options | 8.0.2 | MIT | Options-/Konfigurationsmuster | Core, Client |
| NAudio.Core | 2.3.0 | MIT | Audio-Format-/Buffer-Primitive (geräteunabhängig) | Core, Audio.Linux |
| NAudio | 2.3.0 | MIT | Windows-Audio-Geräteintegration | Audio.Windows |
| PortAudioSharp2 | 1.0.6 | Apache-2.0 | Managed-Bindings an native PortAudio (Linux-Audio) | Audio.Linux |

Hinweise zur Kopplung:

- **Der Pflicht-Kern (`Core`) zieht nur zwei „echte" Fremdbibliotheken auf dem kritischen Pfad:**
  BouncyCastle (Krypto) und Concentus (Opus-Codec). Alles Weitere sind Microsoft-Abstraktionspakete
  (MIT) plus DnsClient (Apache-2.0). Es gibt **keine** Fremd-Protokoll-Engine.
- BouncyCastle wird ausschließlich in `src/Core/Infrastructure/Dtls/*` verwendet (11 Dateien) plus
  einem geteilten Krypto-Helper `src/Core/Infrastructure/Common/Protocols/ProtocolCommonUtilities.cs`.
  Kein Import in SIP/RTP/SDP/STUN/TURN-Parsing (dort ist der Krypto-frei geschriebene Eigencode).
- Concentus wird an **genau einer Stelle** verwendet: `src/Core/Application/Media/Sessions/OpusPayloadCodec.cs`.
  Opus ist damit sauber gekapselt (austauschbar hinter dem Codec-Port).
- Die Audio-Adapter (`NAudio`/`PortAudioSharp2`) sind **optional** und plattformgetrennt; ein Consumer,
  der eigene Audio-I/O anbindet, benötigt sie nicht. PortAudioSharp2 bindet die native **PortAudio**-
  Bibliothek (MIT-artige PortAudio-Lizenz).
- **Build-/Test-only-Pakete** (xUnit, Microsoft.NET.Test.Sdk, coverlet — MIT/Apache-2.0) werden
  **nicht** mit dem SDK ausgeliefert und sind daher nicht IP-relevant für den Vertrieb.

---

## 3. Lizenz des SDK selbst, Namensräume, Marke

- **SDK-Lizenz:** **Apache License, Version 2.0.** Belegt durch `LICENSE` im Repo-Root
  (Kopfzeile: *„Copyright 2026 Bechstein.Digital Ecommerce UG (haftungsbeschränkt) — Licensed under
  the Apache License, Version 2.0"*). Die Lizenzdatei wird über `src/Directory.Build.props`
  (`<PackageLicenseFile>LICENSE</PackageLicenseFile>`) in jedes NuGet-Paket eingebettet.
- **Rechteinhaber / Autor / Company:** `Bechstein Digital` bzw. `Bechstein.Digital Ecommerce UG
  (haftungsbeschränkt)` (aus `LICENSE` und `src/Directory.Build.props`: `Authors`/`Company`).
- **Repository/Projekt-URL:** `https://github.com/BechsteinDigital/CalloraVoipSdk` (aus
  `src/Directory.Build.props`).
- **Aktuelle Versionsbasis:** `VersionPrefix 4.6.0`, lokaler Fallback-Suffix `preview.2`; die
  Release-Version wird im CI aus dem Git-Tag gesetzt (`src/Directory.Build.props`).
- **Marken-/Produktname:** **Callora** (`Product = CalloraVoipSdk`). Der Namensraum-Stamm ist
  durchgängig `CalloraVoipSdk.*` (z. B. `CalloraVoipSdk.Core.Infrastructure.*`,
  `CalloraVoipSdk.Client`, `CalloraVoipSdk.WebRtc`, `CalloraVoipSdk.Hosting`). PackageIds folgen
  dem Projektnamen (`$(MSBuildProjectName)`). **Hinweis:** „Callora" ist ein Produkt-/Markenname —
  ein etwaiger markenrechtlicher Schutzstatus ist aus dem Repository **nicht** ableitbar und gehört
  in die juristische Prüfung (siehe H3).

---

## 4. Lizenz-Kompatibilität (Kernaussage für DD)

- Das SDK steht unter **Apache-2.0**. Alle mitgelieferten Laufzeit-Fremdkomponenten stehen unter
  **permissiven** Lizenzen: **MIT**, **BSD-3-Clause** oder **Apache-2.0**.
- **Kein Copyleft.** Es sind **keine** GPL-, LGPL-, MPL- oder EPL-Abhängigkeiten im Auslieferungspfad.
  Das ist explizit in `THIRD-PARTY-NOTICES.md` (Z. 5–8) so festgehalten und deckt sich mit der
  vollständigen Paketliste in §2 (jedes Paket dort ist permissiv).
- MIT/BSD-3-Clause/Apache-2.0 sind mit einer Weiterverteilung unter Apache-2.0 kompatibel; die
  Pflichten beschränken sich auf **Namensnennung/Copyright-Erhalt** (bei BSD-3-Clause zusätzlich die
  „no endorsement"-Klausel). Diese Attributionspflichten werden über `THIRD-PARTY-NOTICES.md`
  erfüllt, das die Copyright- und Lizenztexte reproduziert.

**Für einen Käufer bedeutet das:** keine viralen/ansteckenden Lizenzpflichten, keine Pflicht zur
Offenlegung des eigenen Quellcodes, keine Feld-der-Nutzung-Einschränkungen aus den Abhängigkeiten.
Die verbleibende laufende Pflicht ist die Attribution (Mitliefern der Notices/Lizenztexte).

---

## 5. Abhängigkeitstabelle — Kurzform (Attribution)

Verifiziert gegen `THIRD-PARTY-NOTICES.md` (Repo-Root):

| Komponente | Lizenz | Copyright-Halter (Kurz) |
|---|---|---|
| BouncyCastle.Cryptography | MIT [H2] | © 2000–2025 The Legion of the Bouncy Castle Inc. |
| Concentus | BSD-3-Clause | © Xiph.Org, Skype, CSIRO, Microsoft u. a. (Opus-Port) |
| DnsClient.NET | Apache-2.0 | © 2024 Michael Conrad |
| NAudio / NAudio.Core | MIT | © Mark Heath & NAudio-Contributors |
| PortAudioSharp2 (+ native PortAudio) | Apache-2.0 / MIT-style | © 2019 B. N. Summerton; © Xiaomi (csukuangfj) |
| Microsoft.Extensions.* | MIT | © .NET Foundation and Contributors |

---

## 6. Ehrliche IP-Risiko-Hinweise

- **(H1) Blind-Copy-Ausschluss ist nicht forensisch belegt.** Nachgewiesen ist: keine SipSorcery-
  Laufzeit-/Build-Abhängigkeit und kein SipSorcery-Import im Quellcode (nur ein Doku-Kommentar).
  Ein struktureller Code-Ähnlichkeitsvergleich gegen SipSorcery (oder andere Referenz-Stacks) zum
  positiven Ausschluss von Blind-Copy wurde **nicht** durchgeführt. Milderndes Faktum: SipSorcery
  ist selbst **BSD-3-Clause** (permissiv) — selbst bei referenzierten Mustern entstünde kein
  Copyleft; das Rest-Risiko ist Attribution/Urheber, nicht Lizenz-Ansteckung. Empfehlung: in die
  juristische Prüfung aufnehmen.
- **(H2) BouncyCastle-Lizenzlabel „MIT" ist leicht vereinfacht.** BouncyCastle steht unter einer
  **an MIT angelehnten** hauseigenen Lizenz (Legion of the Bouncy Castle). Der Volltext ist in
  `THIRD-PARTY-NOTICES.md` (Z. 41–56) verbatim reproduziert und ist praktisch MIT-äquivalent
  (permissiv, keine Copyleft-Wirkung). Als „MIT" zu führen ist vertretbar; für die formale DD sollte
  die Bezeichnung ggf. auf „MIT-style (Bouncy Castle License)" präzisiert werden.
- **(H3) Markenstatus „Callora" ungeprüft.** Aus dem Repository ist kein Nachweis eines
  Marken-/Wortmarkenschutzes ableitbar. Namensrechte gehören in die juristische/kaufmännische DD.
- **(H4) Transitive Abhängigkeiten nicht separat auditiert.** Diese Aufstellung listet die direkten
  `PackageReference`-Einträge. Die genannten Pakete sind bewusst schlank (v. a. `*.Abstractions`),
  ein transitiver Lizenz-Scan (z. B. `dotnet list package --include-transitive` + SBOM) ist für die
  formale DD empfehlenswert, wurde hier aber nicht durchgeführt.
- **(H5) Native PortAudio nur im Linux-Audio-Adapter.** `PortAudioSharp2` lädt eine native
  PortAudio-Bibliothek (MIT-artig). Sie betrifft nur `CalloraVoipSdk.Audio.Linux`, nicht den Kern.
  Native Distribution kann plattform-/packaging-spezifische Pflichten mitbringen — für die
  Auslieferungslogistik relevant, nicht für die Lizenzart.

**Gesamteinschätzung (technisch, nicht juristisch):** Die IP-Struktur ist für einen Asset-Kauf
günstig — Eigen-Stack ohne Fremd-Protokoll-Engine, ausschließlich permissive Abhängigkeiten, kein
Copyleft, sauber gekapselte und austauschbare Krypto-/Codec-Bibliotheken. Die offenen Punkte (H1–H5)
sind Bestätigungs-/Formalisierungsschritte für die juristische Prüfung, keine erkennbaren
Blocker.

---

## 7. Belege / Quellen

- `LICENSE`, `THIRD-PARTY-NOTICES.md` (Repo-Root)
- `src/Directory.Build.props` (Version, Autor, Company, `PackageLicenseFile`, PackageTags)
- `src/**/*.csproj` (alle `PackageReference` — vollständige Liste in §2)
- Internes Produkt-Memory, Abschnitt „Strategic Technology Direction" (Z. 54–57) — intern, nicht Teil des Pakets (auf Anfrage/NDA)
- Code-Erdung via graphify: eigene Wire-Codecs/Parser unter `src/Core/Infrastructure/{Sip,Sdp,Rtp,Stun,Turn,Srtp}/*`;
  BouncyCastle nur unter `src/Core/Infrastructure/Dtls/*` (+ `Common/Protocols/ProtocolCommonUtilities.cs`);
  Concentus nur in `src/Core/Application/Media/Sessions/OpusPayloadCodec.cs`
- ADRs: `../../adr/ADR-028-dtls-srtp-foundation.md`, `../../adr/ADR-049-opus-codec-integration-concentus.md`
- Referenz: `../../reference/semver-policy.md`, `../../reference/README.md`
- Schwester-Seiten im DD-Paket: `./architecture.md`, `./protocol-conformance.md`,
  `./capabilities-matrix.md`, `./quality-and-testing.md`
