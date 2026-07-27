# Spec: SDK-seitige mDNS-Candidate-Auflösung (WebRTC GA-Reifung Paket 2)

**Status:** Freigegeben (User 2026-07-27) · **Datum:** 2026-07-27 · **Branch:** `feat/webrtc-mdns-resolution` (gestapelt auf `feat/webrtc-browser-interop`/Paket 1) · **Teil von:** WebRTC Preview→GA-Reifung, Paket 2

## 1. Kontext & Ziel

Paket 1 (Browser-Interop) hat bewiesen: die `CalloraVoipSdk.WebRtc`-Fassade interoperiert mit echtem Chrome — **aber nur mit im Browser deaktiviertem mDNS**. Per Default verschleiert Chrome/Firefox lokale IPs als `.local`-mDNS-ICE-Candidates (RFC 8828, Privacy). Der SDK **droppt `.local`-Candidates still** (`WebRtcPeerConnection.AddIceCandidateAsync` → `ParseTrickleCandidate` → `IPAddress.TryParse` scheitert → `null` → verworfen; Log „mDNS resolution is not yet supported"). Ein Kunde mit Default-Browser käme so **nicht** zustande, wenn die Verbindung auf host-Candidates angewiesen ist (P2P im selben LAN).

**Ziel Paket 2:** empfangene `.local`-Candidates **auflösen** statt droppen, sodass die Fassade mit Default-Browsern interoperiert. **Erste echte `src/`-Änderung** — klein, am ICE-Kern lokalisiert. Muster = SIPSorcery (Seam + `System.Net.Dns`-Default).

**Warum Resolution-only (kein Publizieren):** mDNS hat zwei Rollen — *Publizieren* (eigene IP hinter `.local` verstecken; braucht einen mDNS-*Responder*) und *Auflösen* (empfangenen `.local`-Namen zur echten IP). Unser SDK ist Server-/Callee-seitig und sendet echte IPs (kein Privacy-Bedürfnis), empfängt aber `.local` vom Browser → es braucht **nur die Auflöse-Hälfte**. RFC 8828 §3.2.2 erlaubt für die Auflösung ausdrücklich einen „hostname resolver that transparently supports both Multicast and Unicast DNS" (= OS-Resolver). Browser/Pion bauen einen eigenen mDNS-Stack, weil sie *publizieren* müssen — wir nicht.

**Nicht-Ziel (Paket 2):** eigenes Publizieren von `.local`-Candidates · ein eigener Multicast-DNS-Query (der Seam erlaubt ihn später als alternative Impl) · die übrigen GA-Blocker (Answerer-Relay-Gap, TCP/TLS-Config-Diagnostik, Video-Stats).

## 2. Der Fix-Punkt (erhoben)

`WebRtcPeerConnection.AddIceCandidateAsync(string candidate, ct)` (`src/Core/Infrastructure/WebRtc/WebRtcPeerConnection.cs`) ruft `ParseTrickleCandidate(candidate)` → `(IPEndPoint Endpoint, long Priority)?`. Dort parst `SdpIceCandidate.TryParse(value)` die Felder (`Address`, `Port`, `Priority`, `Component`, `Transport`); der Drop passiert an `IPAddress.TryParse(parsed.Address, out var ip)` — bei `.local` ist `parsed.Address` ein Hostname, `TryParse` scheitert → `null`. Nach erfolgreichem Parse geht es weiter: buffer in `_pendingRemoteCandidates` (wenn `_session == null`, unter `_sync`) oder `session.AddRemoteCandidate(endpoint, priority)`.

**Der Fix:** wenn `IPAddress.TryParse` scheitert UND `parsed.Address` ein `.local`-Name ist → über den Resolver auflösen → mit der aufgelösten IP `new IPEndPoint(ip, parsed.Port)` **denselben Weg** (buffer/AddRemoteCandidate). Danach ist es ein normaler host-Candidate.

## 3. Architektur & Komponenten

- **`IMdnsResolver`** (neuer Seam, `src/Core/Infrastructure/Common/Network/`): `Task<IPAddress?> ResolveAsync(string hostname, CancellationToken ct)`. Gibt genau **eine** IP zurück oder `null` (nicht auflösbar / Timeout / RFC-Regel verletzt).
- **`SystemMdnsResolver : IMdnsResolver`** (Default-Impl, gleicher Namespace): `Dns.GetHostAddressesAsync(hostname, ct)` mit injizierbarem Timeout (Default 3 s via linked CTS). Wendet die **RFC-8828-Pflichtregeln** an: (a) Name muss `uuid.local` sein — endet auf `.local` und enthält **genau einen Punkt**; (b) liefert die Auflösung **mehr als eine IP → `null`** (Anti-Spoofing, RFC 8828 §3.2.2 „SHOULD ignore candidates where hostname resolution returns more than one IP address"). Exceptions (SocketException etc.) → `null`.
- **`WebRtcPeerConnection`** (Änderung): neues optionales ctor-Feld `IMdnsResolver` (Default `new SystemMdnsResolver()`), gehalten als `_mdnsResolver`. In `AddIceCandidateAsync`: bei Parse-Fail-mit-`.local` startet eine **Hintergrund-Auflösung** (fire-and-forget, an einen peer-lebensdauer-gebundenen `CancellationToken` gekoppelt); bei Erfolg wird der aufgelöste Endpoint über dieselbe buffer/AddRemoteCandidate-Logik eingespeist (unter `_sync`). `AddIceCandidateAsync` kehrt weiterhin sofort zurück (RFC-8838-Trickle-Modell — Candidates kommen asynchron dazu; der Signaling-Pfad wird nicht blockiert).
- **Verdrahtung:** `WebRtcClient`/`WebRtcSessionFactory` reicht den Default-`SystemMdnsResolver` durch; über die WebRTC-Options optional überschreibbar (SIPSorcery-Muster — ein Anwender kann einen eigenen Resolver, z. B. einen Multicast-Query für Hosts ohne OS-mDNS, injizieren). Verhaltensbewahrend bei Default.

## 4. Datenfluss & Timing

`.local`-Candidate empfangen → Parse scheitert an `IPAddress.TryParse` → `.local`-Erkennung → **Hintergrund-Task**: `_mdnsResolver.ResolveAsync(host, peerCt)` (Timeout 3 s). Erfolg → `new IPEndPoint(ip, port)` → unter `_sync`: buffer (Session noch null) oder `session.AddRemoteCandidate(endpoint, priority)`. Misserfolg/Timeout/>1-IP → Candidate verworfen, Debug-Log. Alle anderen Candidates (host/srflx/relay) laufen unbeeinflusst weiter; ICE nominiert wie gehabt das erste erreichbare Paar.

**Lifecycle:** Die Hintergrund-Auflösungen sind an den Peer-`CancellationToken` gebunden (bestehender Dispose-Pfad); beim Dispose werden laufende Auflösungen abgebrochen. Keine unbeobachteten Tasks über die Peer-Lebensdauer hinaus.

## 5. Verhaltensbewahrung & Fehlerbehandlung

- **Kein Regress:** echte-IP-Candidates nehmen exakt den bisherigen Pfad (Resolver nicht aufgerufen). Auf Hosts ohne OS-mDNS scheitert die Auflösung → Candidate verworfen **wie heute**. Der Default-Fall (kein `.local`) ist byte-identisch.
- **Robust:** Timeout, >1 IP, Exceptions, Nicht-`uuid.local` → alle → `null` → verworfen, kein Crash, Debug-Log (wie der bestehende Drop-Log, nur jetzt nach dem Auflöse-Versuch).

## 6. Verifikation

- **Unit-Tests** (gemockter `IMdnsResolver`): (a) `.local` → Resolver aufgerufen, aufgelöste IP → `AddRemoteCandidate` mit korrektem Port/Priority; (b) `SystemMdnsResolver` RFC-Regeln: >1 IP → `null`, Name mit mehr als einem Punkt → `null`, Timeout → `null`; (c) echte-IP-Candidate → Resolver **nicht** aufgerufen (Pfad unverändert); (d) Buffering: `.local` vor Session-Existenz → nach Auflösung gepuffert, bei Session-Start eingespeist.
- **E2E (measure-first-Kern):** der Paket-1-Browser-Interop-Test mit **mDNS im Browser AKTIVIERT** (`--disable-features=WebRtcHideLocalIpsWithMdns`-Flag **entfernt**) → connect + bidir Audio klappt trotz `.local`-Candidates. Beweist, dass `System.Net.Dns` echte Chromium-`uuid.local`-Candidates auf diesem Host auflöst.

## 7. Zentrales Risiko (measure-first, erster Plan-Task)

Ob `Dns.GetHostAddressesAsync` einen **ephemeren Chromium-`uuid.local`** auflöst (nicht nur einen statischen avahi-Namen), ist noch nicht bewiesen — Chromium hat einen eigenen mDNS-Responder. **Der allererste Plan-Task ist ein Spike:** in einem `[BrowserRequiredFact]` einen echten Chromium-`.local`-Candidate einsammeln und mit `Dns.GetHostAddressesAsync` aufzulösen versuchen. Klappt es → Option A trägt, die Seam-Maschinerie wird gebaut. Klappt es NICHT → wir wissen es *vor* dem Aufwand und die Default-Resolver-Impl wechselt auf einen eigenen Multicast-DNS-Query (hinter demselben `IMdnsResolver`-Seam — Architektur unverändert). Der Spike entscheidet die Default-Impl, nicht die Architektur.

## 8. Entscheidungen

- `// DECISION:` **Resolution-only** (kein Publizieren) — unser SDK versteckt seine IP nicht; RFC 8828 §3.2.2 erlaubt OS-Resolver für Auflösung.
- `// DECISION:` **`IMdnsResolver`-Seam + `SystemMdnsResolver` (`System.Net.Dns`) als Default** — exakt das SIPSorcery-Muster; Seam macht es testbar (mockbar) + später auf einen eigenen Multicast-Query erweiterbar ohne Kernumbau.
- `// DECISION:` **RFC-8828-Pflichtregeln** in der Default-Impl: genau-ein-Punkt (`uuid.local`) + >1 IP → ignorieren.
- `// DECISION:` **Fire-and-forget-Auflösung** (peer-lifecycle-gebunden, Timeout 3 s) — blockiert den Signaling-Pfad nicht (RFC-8838-Trickle).
- `// DECISION:` **Verhaltensbewahrend bei fehlender Auflösung** — Candidate verworfen wie heute, kein Regress.
- `// DECISION:` **measure-first-Spike als erster Plan-Task** (löst der OS-Resolver echte Chromium-mDNS-Namen?), bevor die Seam-Maschinerie gebaut wird.
