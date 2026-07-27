# Spec: WebRTC-Browser-Interop-Nachweis (GA-Gate, Paket 1)

**Status:** Freigegeben (User 2026-07-27) · **Datum:** 2026-07-27 · **Branch:** `worktree-feat+webrtc-release` (von origin/main `07c21be`) · **Teil von:** WebRTC Preview→GA-Reifung, Paket 1 von 4

## 1. Kontext & Ziel

Die `CalloraVoipSdk.WebRtc`-Fassade ist erklärter Preview-Stand (`4.6.0-preview.*`). Der Code ist architektonisch weit (ICE-Trickle, DTLS-SRTP, BUNDLE, TURN-Relay), aber **kein Test fährt je gegen einen echten Browser** — alle Peer-Tests sind SDK↔SDK. Bis dieser Nachweis steht, ist jeder „production-ready"-Claim ungedeckt. **Browser-Interop ist der GA-Gate.**

**Ziel Paket 1:** Beweisen, dass die Fassade mit einem echten Browser interoperiert — volle Kette Signaling→ICE→DTLS→SRTP→Media, **Audio (Opus) bidirektional**, **SDK-Offerer ↔ headless-Chrome-Answerer**. Das ist der minimale VOLLSTÄNDIGE Interop-Beweis (entspricht dem Media-Lib-Referenzmuster: Pion/aiortc fahren echten Chrome via Playwright/Selenium mit fake-device + getStats; wir prüfen Audio statt DataChannel, weil das SDK Media und kein SCTP hat).

**Nicht-Ziel (Paket 1):** Video (VP8), Browser-Offerer-Richtung, Stats-Detailabgleich, weitere Browser (Firefox), SCTP/DataChannel. Diese werden durch den ersten Nachweis priorisiert. **Keine `src/`-Änderung** — deckt der Test einen echten Interop-Bug auf, wird der als eigenes GA-Paket gefixt.

## 2. Fassaden-Signaling-Surface (erhoben)

`IWebRtcClient.CreatePeer() → IPeerConnection`. Alle SDP/Candidate sind **plain strings** (RFC 8829/8445):
- `string CreateOffer()` (synchron, bindet den Media-Socket, emittiert host-Candidate)
- `Task<string> SetRemoteDescriptionAsync(string answerSdp, ct)`
- Trickle: `event EventHandler<string> LocalIceCandidateDiscovered` · `Task AddIceCandidateAsync(string, ct)` · `Task GatherCandidatesAsync(ct)` (vor StartAsync; für host-only nicht nötig)
- `Task StartAsync(ct)` (ICE→DTLS→Receive-Loop)
- Media: `ValueTask SendAudioAsync(ReadOnlyMemory<byte>, ct)` · `event EventHandler<RemoteTrack> TrackReceived` → `RemoteTrack.FrameReceived` → `EncodedFrame` (`byte[] payload`)
- State: `event EventHandler<PeerConnectionState> ConnectionStateChanged` (New→Connecting→**Connected**→…)

Default-Audio = Opus (PT 111, browser-nativ). DTLS: fresh ECDSA-P256 self-signed, Fingerprint korrekt-by-construction im SDP; der Offerer wird DTLS-Server (`actpass`, Browser wählt `active`). Beide Peers auf localhost → **host-Candidates reichen, kein STUN/TURN**.

## 3. Architektur & Komponenten

Neues, isoliertes Projekt `tests/CalloraVoipSdk.BrowserInteropTests` (net10.0, `Microsoft.Playwright`-NuGet) — konsistent mit dem `InteropTests`-Muster (eigene Package-Deps + CI-Kategorie, nicht in die Unit/Integration-Matrix).

- **`BrowserInteropSignalingBridge`** — minimaler in-process HTTP+WS-Server auf `System.Net.HttpListener` (keine ASP.NET-Dependency): serviert die statische `peer.html` unter `/` und einen WebSocket unter `/ws`. Reicht JSON-Nachrichten `{type: offer|answer|candidate|stats, ...}` zwischen dem SDK-Peer (C#) und dem Browser durch. Das ist das einzige neue Infra-Stück (der Bestand hat keinen WS-Server).
- **`peer.html` + Inline-JS** — `RTCPeerConnection` als **Answerer**: `getUserMedia({audio:true})` (Chrome-fake-device → synthetisches Opus), auf `offer`→`setRemoteDescription`+`createAnswer`+`setLocalDescription`→`answer` über WS; Trickle: `onicecandidate`→WS, eingehende `candidate`→`addIceCandidate`; pollt periodisch `pc.getStats()`, findet den `inbound-rtp`(kind=audio)-Report und meldet `{type:stats, bytesReceived, packetsReceived}` über WS.
- **`BrowserPeer`** (C#, Playwright-Wrapper) — startet Chromium **headless** mit Args: `--use-fake-device-for-media-stream`, `--use-fake-ui-for-media-stream`, `--disable-features=WebRtcHideLocalIpsWithMdns` (s. §6), navigiert zur Bridge-URL; `IAsyncDisposable` schließt Browser+Context.
- **`BrowserRequiredFactAttribute`** — Gate analog `DockerRequiredFact`: skippt, wenn das Playwright-Chromium nicht auffindbar ist. Trägt Kategorie `BrowserInterop`.

## 4. Datenfluss

**Signaling (über die WS-Bridge):**
1. Test baut SDK-Peer (Offerer), `offer = peer.CreateOffer()`.
2. `peer.LocalIceCandidateDiscovered += (…) → Bridge.SendToBrowser(candidate)`.
3. Browser verbindet WS → Bridge sendet `offer` → Browser `setRemoteDescription`/`createAnswer` → `answer` → Bridge → `peer.SetRemoteDescriptionAsync(answer)`.
4. Browser `onicecandidate` → Bridge → `peer.AddIceCandidateAsync(candidate)`; SDK-Candidates umgekehrt.
5. `peer.StartAsync()` → ICE-Checks (host↔host) → DTLS-Handshake → `Connected`.

**Media — Echo-Muster (kein Opus-Encoder nötig):** Das SDK ist transport-only. Der Browser sendet via fake-device echtes Opus; das SDK empfängt es (`RemoteTrack.FrameReceived`) **und echoed jeden Frame sofort via `SendAudioAsync` zurück**. Ein Datenfluss beweist beide Richtungen: SDK-Empfang (RemoteTrack feuert) + Browser-Empfang (`getStats bytesReceived>0`). Kein Encoder, keine Testdatei nötig.

## 5. Verifikation (Assertions)

Ein `[BrowserRequiredFact]`-Test, harte Assertions mit Deadline (~20–30 s, headless-ICE-tolerant):
- **(a) SDK verbindet:** `peer.ConnectionStateChanged` erreicht `PeerConnectionState.Connected`.
- **(b) Browser→SDK Audio:** `RemoteTrack.FrameReceived` feuert ≥ N Frames (N konservativ, z. B. ≥ 20 = ~0,4 s Opus).
- **(c) SDK→Browser Audio:** der Browser meldet über WS `inbound-rtp.bytesReceived > 0 && packetsReceived ≥ N` (aus `getStats`).

Alle drei müssen grün sein = volle bidirektionale Interop-Kette gegen einen echten Browser bewiesen.

## 6. measure-first-Befund: mDNS (proaktiv adressiert)

Chrome verschleiert lokale IPs per Default als `.local`-mDNS-ICE-Candidates (Privacy). Der Recon zeigte: das SDK **droppt `.local`-Candidates still** (`WebRtcPeerConnection.cs:507`). Ohne Gegenmaßnahme → Browser schickt nur `.local` → SDK ignoriert → keine Verbindung. **Paket-1-Lösung:** mDNS im Test-Browser deaktivieren (`--disable-features=WebRtcHideLocalIpsWithMdns`) → echte host-IP-Candidates fließen. **mDNS-Auflösung IM SDK ist ein separates, kleineres GA-Item** (das SDK sollte `.local`-Candidates auflösen statt droppen, damit es mit Default-Browsern ohne Flag interoperiert) — durch diesen Test motiviert, hier NICHT gebaut, im Register notiert.

## 7. CI & Verhaltensbewahrung

- **Lokal-first (Lehre aus dem FreeSWITCH-CI-Bug):** Kategorie `BrowserInterop`, explizit aus **allen** Nicht-Browser-Jobs ausgeschlossen — Haupt-Test-Job (`ci.yml`), Release-Gate (`packages.yml`) UND der Interop-Docker-Job je um `&Category!=BrowserInterop` erweitert. Läuft lokal (Playwright-Chromium installiert). **Aufnahme ins PR-CI-Gate = separater Folge-Schritt** (Playwright-Browser-Install-Step in ci.yml), sobald über mehrere Läufe stabil.
- **Keine `src/`-Änderung.** Der Test darf keine bestehende Suite beeinflussen; das neue Projekt kommt in die `.sln`, aber alle seine Tests sind `BrowserInterop`-gegatet.

## 8. Entscheidungen

- `// DECISION:` Browser-Steuerung via **Playwright .NET** (`Microsoft.Playwright`), nicht Node-Subprozess — eine Sprache/ein Prozess, Chromium schon installiert.
- `// DECISION:` **Echo-Muster** für Media statt Opus-Encoder — SDK transport-only, echte Browser-Opus-Frames zurückspielen beweist beide Richtungen ohne Encoder-Dependency.
- `// DECISION:` **HttpListener** statt ASP.NET Core für die Signaling-Bridge — minimal, keine Web-Framework-Dependency im Test.
- `// DECISION:` **mDNS im Browser deaktivieren** für Paket 1; SDK-seitige mDNS-Auflösung = separates GA-Item.
- `// DECISION:` **host-only ICE** (kein STUN/TURN) — beide Peers auf localhost.
- `// DECISION:` Scope Paket 1 = Connect + bidir. Audio, SDK-Offerer. Video/Browser-Offerer/mehr Browser = Folge-Slices.
- `// DECISION:` Neues Projekt `CalloraVoipSdk.BrowserInteropTests`, Kategorie `BrowserInterop`, lokal-first.
