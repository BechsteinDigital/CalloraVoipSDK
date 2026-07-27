# ADR-009: WebRTC Browser-Peer Roadmap

Status: Accepted  
Date: 2026-07-15

## Context

Callora (Host/CPaaS) soll WebRTC als eigenes Modul auf der SDK-Engine anbieten — die Engine
selbst soll ein WebRTC-**Peer** sein (Browser ⇄ SDK), nicht bloß ein SIP-Stack. Referenzen:
**SipSorcery** (OSS, .NET) = nativer WebRTC-Peer-Stack; **Ozeki** = WebRTC nur als Gateway
(Browser ⇄ Server ⇄ SIP), kein nativer C#-Peer. Entschieden ist der Peer-Weg.

Eine Code-Verifikation (2026-07-15, sechs parallele Explore-Läufe) hat den Ist-Stand gegen
die WebRTC-Peer-Checkliste kartiert; die daraus abgeleitete Roadmap wurde vom Founder mit
sieben Architektur-Präzisierungen geschärft (siehe Decision).

## Ist-Stand (verifiziert: Code + Tests vorhanden — NICHT browser-interop-validiert)

Vorhanden & reif: **DTLS-SRTP** (Handshake beide Rollen, RFC-5764-Key-Export, Fingerprint,
E2e-Tests), **rtcp-mux** (Audio + Video), **WebRTC-SDP-Profil** (`UDP/TLS/RTP/SAVPF`,
`fingerprint`, `setup`, `ice-ufrag/pwd`, `ice-options:trickle`, `extmap`, `rtcp-fb`,
`candidate`), **ICE** (RFC 8445, checks, nomination, consent freshness RFC 7675,
`IceSnapshot`), **RTP/RTCP** (transport-cc, NACK/PLI, RTX), **Opus** nativ (RFC 7587).

Teilweise / Lücke: **BUNDLE** (SDP-Infra da, aber kein gemeinsamer Transport — siehe unten),
**Track-Identity** (`a=msid`/`a=ssrc` fehlen), **ICE-Connection-State-Event** (nur finaler
`IceSnapshot`; Consent-Loss ceaset intern ohne App-Event).

Fehlt: **SCTP Data Channels**, **TCP/TURN-TCP-Pfade**, **native Video-Codecs** (VP8/H264).

## Decision

Der WebRTC-Transport-Kern ist zu ~drei Vierteln vorhanden, aber BUNDLE und die WebRTC-Peer-
Fassade sind echte Neubauten, kein Feintuning. Reihenfolge und Schnitt:

### 1. ICE-Connection-State-Events (MVP-Vorbedingung, zuerst)
Der finale `IceSnapshot` reicht für Diagnose, nicht für eine laufende Browserverbindung.
Öffentlich nötig: laufende Transport-Zustandsänderungen, ausgewähltes Candidate-Pair,
Consent-Verlust, **Wiederherstellung**, dauerhafter Fehler, und die Möglichkeit für **ICE-
Restart oder kontrollierte Terminierung**. Heute beendet Consent-Loss nur intern die
Übertragung (`RtpCallMediaSession.OnMediaConsentLost` → `StopTransmission`); die App erfährt
nichts. Muss vor dem öffentlichen WebRTC-Paket behoben sein.

### 2. BUNDLE als Transport-Architekturumbau (zentraler Brocken)
RFC 8843 verlangt einen **gemeinsamen Transport / ein 5-Tupel** für mehrere `m=`-Sektionen,
eine gemeinsame ICE-Kandidatengruppe und eine gemeinsame RTP-Session; eingehende Streams
werden über MID / PT / ggf. SSRC der Media-Sektion zugeordnet. Heute hat Video einen eigenen
`RtpSession`, eigenen Port, eigene DTLS-Assoziation und eigenes ICE/Consent auf dem Video-
5-Tupel. `Bundle = true` in der SDP-Generierung wäre technisch falsch. Nötig ist:

```
BundledMediaTransport
├── ein ICE Agent / ausgewähltes Candidate Pair
├── ein UDP-Socket
├── eine DTLS-Assoziation
├── gemeinsame SRTP/SRTCP-Kontexte
├── RTP/RTCP/STUN/DTLS-Demultiplexing
└── Track-Routing über MID / PT / SSRC
    ├── AudioTrack
    └── VideoTrack
```

### 3. Track-Identity (auf BUNDLE aufbauend) — msid und ssrc getrennt
Zwei Ebenen, nicht ein Baustein:

```
Track Identity            RTP Source Identity
├── MID                   ├── SSRC
├── Stream ID             ├── RTX SSRC
├── Track ID              └── optional RID / Simulcast
└── MSID
```

Browser-MVP: **ein Audio-Track + ein Video-Track**, stabile MID-Werte, `a=msid`, **MID-RTP-
Header-Extension**, korrektes BUNDLE-Routing. Screensharing / mehrere Video-Tracks / Simulcast
folgen danach (JSEP nutzt dafür `a=rid` + `a=simulcast`; eine zweite Video-`m=`-Sektion allein
genügt nicht).

### 4. Signaling-neutrale WebRTC-Fassade — Paket `CalloraVoipSdk.WebRtc`
Browser sprechen nicht mit der SIP-Fassade; die App macht das Signaling (WebSocket/HTTP/
Callora) selbst. Eigene öffentliche Peer-API:

```csharp
var peer = webRtc.CreatePeer(options);
peer.LocalIceCandidateDiscovered += OnCandidate;
peer.ConnectionStateChanged      += OnConnectionStateChanged;
peer.TrackReceived               += OnTrackReceived;
await peer.SetRemoteDescriptionAsync(remoteOffer);
var answer = await peer.CreateAnswerAsync();
await peer.AddRemoteIceCandidateAsync(candidate);
```

Grenze: **SDK** = SDP, ICE, DTLS, SRTP, BUNDLE, RTP, RTCP, Tracks. **Anwendung** =
WebSocket/HTTP-Signaling, Teilnehmer, Räume, Authentifizierung. Damit bleibt WebRTC auch
außerhalb Callora nutzbar.

### 5. Codec-Rollen + FFmpeg-Paket
Nicht das monolithische `IVideoDevice` — für Server/Gateway/Recording/Transcoding zu breit.
Stattdessen getrennte Rollen; `IVideoDevice` bleibt Convenience-Komposition:

```
IVideoSource · IVideoSink · IVideoEncoder · IVideoDecoder
IVideoDevice = Convenience-Komposition der vier
CalloraVoipSdk.Video.FFmpeg → Ffmpeg{Encoder,Decoder,Source,Sink,Device}
```

Codec-Reihenfolge: **VP8 → H.264** (Constrained Baseline, `packetization-mode=1`) → VP9 →
optional AV1 → H.265 als separates optionales Profil. **H.265 nicht im MVP** (nicht im
WebRTC-Pflichtsatz; darf VP8/H.264-Interop nicht verzögern).

### 6. Restriktive Netze: TURN/TCP und TURN/TLS vor ICE-TCP
Direkte ICE-TCP-Candidates (RFC 6544) sind nicht die höchste Priorität — selbst RFC 6544
bevorzugt UDP für RTP. Reihenfolge: **TURN/UDP → TURN/TCP → TURN/TLS-TCP:443** → dann direkte
ICE-TCP-Candidates (RFC 6544) → optional TURN-TCP-Allokationen (RFC 6062, eigenes Verfahren
Relay↔Peer). Vollständig blockiertes UDP ist der Standardgrund für TCP/TLS.

### 7. SCTP Data Channels (späterer Slice, blockiert Medien-MVP nicht)
Vollständiges Zusatz-Subsystem: `m=application` → DTLS → SCTP-Association → mehrere SCTP-
Streams → Partial Reliability → DCEP (`DATA_CHANNEL_OPEN`/`_ACK`) → öffentliche DataChannel-
API. Am besten auf derselben BUNDLE-/DTLS-Transportbasis, nach dem Audio/Video-MVP.

### Browser-MVP (Audio + Video)
= Schritt 1 (ICE-State) + 2 (BUNDLE-Transport) + 3 (Track-Identity) + 4 (WebRtc-Fassade) +
5 (VP8/H.264) + **Browser-Interop-Validierung** (Chrome/Firefox, eigener Meilenstein).
TURN/TCP/TLS (6) parallel für restriktive Netze. Data Channels (7) danach.

## Consequences

Positive: Callora baut WebRTC als Modul (Signaling + App-Logik) auf der Engine; der WebRTC-
Peer bleibt im SDK und außerhalb Callora nutzbar. Audio-Interop ist durch Opus + DTLS-SRTP
schon nah.

Tradeoffs: BUNDLE ist ein echter Transport-Umbau (ein 5-Tupel/DTLS/ICE statt pro m-line) —
berührt RtpSession, DTLS, ICE/Consent, SRTP-Kontexte. „Reif" heißt Code + Tests, nicht
browser-validiert; die Interop-Validierung bleibt Pflicht vor jedem Produktions-Claim.

## Guardrails

- WebRTC-Transport bleibt im SDK; Callora bringt nur Signaling + Anwendungslogik.
- Kein WebRTC-Code im SIP-Signaling-Kern (getrennte Module / eigenes `CalloraVoipSdk.WebRtc`).
- Keine „WebRTC-ready/production"-Claims ohne Browser-Interop-Validierung.
- BUNDLE nicht als SDP-Flag „faken" — nur mit echtem gemeinsamem Transport.
