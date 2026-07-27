# Spec: WebRTC VP8-Video-Browser-Interop (GA-Reifung Paket 3)

**Status:** Freigegeben (User 2026-07-27) · **Datum:** 2026-07-27 · **Branch:** `feat/webrtc-video-interop` (von main, enthält Paket 1; unabhängig von Paket 2/mDNS) · **Teil von:** WebRTC Preview→GA-Reifung, Paket 3

## 1. Kontext & Ziel

Paket 1 hat bewiesen, dass die `CalloraVoipSdk.WebRtc`-Fassade **Audio (Opus)** mit echtem headless Chrome interoperiert. Für einen belastbaren GA-Claim eines Communication-SDK fehlt der entsprechende **Video**-Nachweis — Video-Calls sind ein Kern-Use-Case.

**Ziel Paket 3:** Beweisen, dass die Fassade **VP8-Video** mit echtem Chrome interoperiert — SDK-Offerer ↔ Browser-Answerer, Video **bidirektional**, mit **Dekodier-Nachweis**: der Browser dekodiert das vom SDK gesendete Video tatsächlich (`getStats framesDecoded > 0`), nicht nur RTP-Empfang. Erweitert den Paket-1-Harness. **Keine `src/`-Änderung** (außer der Test deckt einen echten Bug auf → eigenes Paket).

**Scope-Grenze:** **Video-only** (Audio ist in Paket 1 bewiesen — ein fokussiertes Video-Paket ist klarer). **VP8** (browser-nativ, kein Hardware-H264-Problem). SDK-Offerer-Richtung. **Draußen:** Audio+Video kombiniert, H264, Browser-Offerer-Richtung, Simulcast-recv-Demux.

## 2. Video-Fassaden-Surface (erhoben)

- `WebRtcConfiguration`: `EnableVideo = true`, `VideoCodecs = ["VP8"]` (Default sonst H264).
- Senden: `IPeerConnection.SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, ct)` — ein encoded VP8-Frame + RTP-Timestamp.
- Empfangen: `event TrackReceived (RemoteTrack)`; `RemoteTrack.Kind == TrackKind.Video`; `RemoteTrack.FrameReceived (EncodedFrame)` mit `EncodedFrame.Payload` (VP8-Bytes), `.RtpTimestamp`, `.IsKeyFrame`.
- VP8-Packetiser/Depacketiser existieren im Core (`Rtp/Packetisation/Vp8*`).

## 3. Architektur & Komponenten

Erweiterung des Paket-1-Harness in `tests/CalloraVoipSdk.BrowserInteropTests` (Bridge, `peer.html`, `BrowserPeer`, `BrowserRequiredFactAttribute` wiederverwendet).

- **SDK-Config:** der Test baut den `WebRtcClient` mit `EnableVideo = true, VideoCodecs = ["VP8"]` (zusätzlich zu Audio-Opus aus Paket 1 — oder video-only, s. §7). Das SDK empfängt Video via `TrackReceived` (Kind=Video), sendet via `SendVideoFrameAsync`.
- **`peer.html`-Erweiterung:** `getUserMedia({ video: true })` (Chrome-fake-device → synthetisches Testpattern-VP8), Video-Track via `addTrack` (VOR `createAnswer`); der bestehende `getStats`-Report meldet zusätzlich den `inbound-rtp`(kind=video)-Report mit `bytesReceived` **und `framesDecoded`**. Die bestehende Audio-Logik bleibt (oder wird für video-only entfernt, s. §7).
- **Test:** `VP8Video_FlowsAndDecodes_WithRealBrowser` (`[BrowserRequiredFact, Trait("Category","BrowserInterop")]`), Struktur wie der Paket-1-Test (SDK-Offerer, Bridge-Pump, `browserReady`-Gate, `answerApplied`-Gate).

## 4. Datenfluss — Video-Echo mit Keyframe-Bewusstsein

Signaling identisch zu Paket 1 (Offer/Answer/ICE über die Bridge). Media:
- **Browser→SDK:** der Browser sendet via fake-device VP8-Video; das SDK empfängt es (`TrackReceived` Kind=Video → `FrameReceived`).
- **SDK→Browser (Echo):** das SDK echoed die empfangenen VP8-Frames via `SendVideoFrameAsync(payload, rtpTimestamp)` — kein Encoder nötig. **★ Keyframe-Bewusstsein:** Video braucht ein Keyframe zum Dekodieren. Chrome sendet beim Track-Start ein VP8-Keyframe; der Echo-Handler beginnt das Echo **erst ab dem ersten Frame mit `EncodedFrame.IsKeyFrame`** (danach alle Frames), sodass der Browser einen dekodierbaren Stream (Keyframe zuerst) erhält. Der empfangene `RtpTimestamp` wird durchgereicht.

## 5. Verifikation (Assertions, Deadline ~30 s, Poll)

- **(a) Browser→SDK:** `RemoteTrack` (Kind=Video) `FrameReceived` ≥ N VP8-Frames (N konservativ, z. B. ≥ 10).
- **(b) SDK→Browser Transport:** Browser meldet `inbound-rtp(video).bytesReceived > 0` (die VP8-Frames erreichen den Browser über SRTP).
- **(c) SDK→Browser Dekodieren:** Browser meldet `inbound-rtp(video).framesDecoded > 0` — der eigentliche Dekodier-Nachweis (der Browser rekonstruiert ein Bild).

Alle drei grün = bidirektionaler VP8-Video-Interop mit Dekodieren gegen echten Browser bewiesen.

## 6. Zentrales Risiko (measure-first, erster Plan-Task)

Ob das VP8-**Echo** dem Browser einen dekodierbaren Stream liefert (`framesDecoded > 0`) ist das offene Risiko:
- **Keyframe:** hängt davon ab, dass der Echo-Stream mit einem Keyframe beginnt (via `IsKeyFrame`-Flag) und der SDK-Depacketiser das Keyframe-Flag korrekt setzt.
- **Fragmentierung:** VP8-Keyframes sind groß → mehrere RTP-Pakete; der SDK-Packetiser muss korrekt fragmentieren, der Browser reassemblieren.
- **PLI:** der Browser sendet evtl. eine Picture Loss Indication (RTCP), wenn er kein Keyframe hat; ein transport-only-SDK ignoriert PLI evtl. → wenn das erste Frame kein Keyframe war, bleibt der Browser ohne Bild.

**Erster Plan-Task = Spike:** Video-Track aufsetzen, Echo (keyframe-aware) fahren, `getStats framesDecoded` beobachten. Trägt es → Design steht (Assertions a/b/c). Trägt es nicht → früh sichtbar; Fallback-Optionen: (i) das erste Keyframe im Echo periodisch wiederholen; (ii) Rückfall auf Transport-Nachweis (a)+(b) mit dokumentierter Dekodier-Grenze als ehrliches Ergebnis. Der Spike entscheidet die finale Assertion-Tiefe.

## 7. Video-only vs. Audio+Video (Entscheidung)

`// DECISION:` **Video-only** für dieses Paket — der klarste, fokussierteste Video-Nachweis; Audio ist in Paket 1 bewiesen. Die `peer.html` bekommt eine Video-Variante (oder einen `video`-Modus-Schalter); der Test nutzt `getUserMedia({video:true})` ohne Audio, SDK `EnableVideo=true` (Audio-Codecs bleiben angeboten, aber kein Audio-Track vom Browser). Falls der Spike zeigt, dass Chrome ohne Audio-Track Probleme macht, wird Audio als stiller Begleiter ergänzt (measure-first).

## 8. CI & Verhaltensbewahrung

- **Lokal-first:** `Category=BrowserInterop` (schon aus allen Nicht-Browser-CI-Jobs ausgeschlossen — Paket 1). Läuft lokal (Chromium installiert). Aufnahme ins PR-CI-Gate = separater Folge-Schritt.
- **Keine `src/`-Änderung.** Die bestehenden Paket-1-Tests (Audio) bleiben unberührt; `peer.html`/`BrowserPeer`-Erweiterungen sind additiv/parametrisiert.

## 9. Entscheidungen

- `// DECISION:` **VP8** (browser-nativ, kein Hardware-H264-Problem); SDK `VideoCodecs=["VP8"]`.
- `// DECISION:` **Dekodier-Nachweis** (`framesDecoded>0`) als Ziel-Assertion (User-Wahl), nicht nur Transport.
- `// DECISION:` **Echo-Muster mit Keyframe-Bewusstsein** (Echo ab erstem `IsKeyFrame`), kein Encoder.
- `// DECISION:` **Video-only**, SDK-Offerer-Richtung; Audio/Browser-Offerer/Simulcast draußen.
- `// DECISION:` **measure-first-Spike als erster Plan-Task** (trägt das VP8-Echo für Dekodieren?), bevor die Assertion-Tiefe fixiert wird.
