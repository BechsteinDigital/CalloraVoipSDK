# CalloraVoipSdk 4.7.0 — Doku-Widersprüche (aus SFU-Integration gefunden)

> Scratch-Notiz aus der Videokonferenz-Plugin-Integration (Callora SFU, `SfuRoomMediaRouter`).
> Gefunden beim tiefen Lesen der WebRTC-Fassade + Core-Media-Ebene. Untracked — nach dem Fix löschen/verschieben.
> Datum: 2026-07-30. Basis: Working-Tree `release/4.7.0-finalize` (`v4.6.0-38-g62187bb1`).

Zwei Klassen: **A) echte Widersprüche im Code** (Doku behauptet das Gegenteil der ausgelieferten
Implementierung — bitte fixen) und **B) by-design-Impedanzen** (keine Bugs, aber die SFU-relevanten
Schärfen, die man leicht übersieht — Kandidaten für prominentere Doku, kein Codefix nötig).

---

## A) Echte Widersprüche (Codefix)

### A1 — `IVideoTrack` behauptet, Mid-Call-Renegotiation sei NICHT unterstützt  **[High]**

**Datei:** `src/Client/WebRtc/IVideoTrack.cs:4` und `:10–11`

Aktueller Text (öffentliches Interface, landet in IntelliSense):
- Z. 4: „A handle to an outbound video track added **before the first offer** …"
- Z. 10–11: „The multi-track surface is **preview-grade: tracks must be added before `IPeerConnection.CreateOffer`** (mid-call add / renegotiation is a later package)."

**Das ist falsch / veraltet.** Mid-Call-`AddVideoTrack` + Renegotiation ist in 4.7.0 ausgeliefert und end-to-end getestet. Widerlegt durch:

- **Interne Impl** `src/Client/WebRtc/VideoTrack.cs:3–9` sagt es KORREKT: „The MID is fixed at add time from the offer's m-line layout, so a frame can be sent as soon as the transport is keyed." (kein Pre-Offer-Zwang).
- **Schwester-Interface** `src/Client/WebRtc/IAudioTrack.cs:10–17` beschreibt Mid-Call KORREKT: „a track added mid-call is pending until the next offer/answer cycle applies it to the live session (RFC 8829 renegotiation)." → `IVideoTrack` und `IAudioTrack` widersprechen sich gegenseitig.
- **`IPeerConnection.AddVideoTrack` remarks** `src/Client/WebRtc/IPeerConnection.cs:79–82`: „a track added mid-call is pending until the next `CreateOffer`/`SetRemoteDescriptionAsync` cycle applies it to the running session (RFC 8829 renegotiation)." → direkter Widerspruch zu `IVideoTrack`.
- **End-to-End-Test** `tests/CalloraVoipSdk.Core.IntegrationTests/WebRtcRenegotiationPeerToPeerTests.cs:29–112`, Test `Adding_a_second_video_track_mid_call_starts_it_flowing_while_the_first_keeps_flowing`: zwei echte Peers über DTLS-SRTP, offerer ruft NACH Connect `AddVideoTrack` (MID „3") + `CreateOffer` + `SetRemoteDescription`; neue MID beginnt zu fließen, MID „1" läuft ununterbrochen weiter, kein Transport/DTLS/ICE/SRTP-Rebuild (Assert `WebRtcConnectionState.Connected` bleibt, Z. 81).

**Warum wichtig:** Das ist der einzige Punkt, der einen SFU-Autor in die falsche Architektur zwingt.
Die Doku sagt „mid-call join geht nicht" → man würde fälschlich eine Max-Slots-Vorprovisionierung bauen.
Die Wahrheit ist **Join-Anytime via Renegotiation**.

**Fix:** `IVideoTrack.cs` remarks an `IAudioTrack.cs` angleichen (Mid-Call unterstützt); Summary-Zeile 4 „added before the first offer" → neutraler formulieren („added with `IPeerConnection.AddVideoTrack()`").

---

### A2 — `WebRtcSignalingState` behauptet, Re-Offer/Renegotiation sei „a later package"  **[Medium]**

**Datei:** `src/Core/Infrastructure/WebRtc/WebRtcSignalingState.cs:7`

Text: „(re-offer / renegotiation is a later package), so the state moves `Stable → HaveLocalOffer → Stable`".

**Veraltet:** Renegotiation (zweiter Offer/Answer-Zyklus auf laufender Session) ist in 4.7.0 real
(siehe A1-Test). Auch `IPeerConnection.SignalingState` `src/Client/WebRtc/IPeerConnection.cs:34–40`
beschreibt bereits den Answerer-Pfad, der pro `SetRemoteDescriptionAsync` zweimal feuert — d. h. wiederholte
Offer/Answer-Zyklen sind vorgesehen. Der Klammerzusatz „is a later package" stimmt nicht mehr.

**Fix:** Klammerzusatz entfernen oder umformulieren (die Stable→HaveLocalOffer→Stable-Beschreibung selbst bleibt korrekt pro Zyklus).

---

### A3 — `IPeerConnection.AddAudioTrack` Summary suggeriert Pre-Offer-only  **[Low]**

**Datei:** `src/Client/WebRtc/IPeerConnection.cs:100` (Summary `AddAudioTrack()`) und `:118` (Summary Options-Overload)

Beide Summaries sagen „… **before the first offer** …". Die zugehörigen `<remarks>` (Z. 104–111)
korrigieren das („a track added mid-call is pending until the next offer/answer cycle …"), aber die
Summary allein (die IntelliSense primär zeigt) unterstellt Pre-Offer-Zwang. Die `AddVideoTrack`-Summaries
(Z. 72–74, 88–91) haben diese Einschränkung nicht → interne Inkonsistenz.

**Fix:** „before the first offer" aus den beiden `AddAudioTrack`-Summaries streichen (an `AddVideoTrack` angleichen).

---

## B) By-design-Impedanzen (keine Bugs — nur SFU-Integrationswissen)

Diese sind KORREKT implementiert und teils dokumentiert; kein Codefix nötig. Nur festgehalten, damit
sie nicht versehentlich als Bug „gefixt" werden, und als Kandidaten für prominentere SFU-Doku.

### B1 — Payload-Lebensdauer bei `RemoteTrack.FrameReceived` / `EncodedFrame.Payload`

`EncodedFrame.Payload` ist laut Doku (`src/Client/WebRtc/EncodedFrame.cs:19`) nur **während des
`FrameReceived`-Callbacks gültig**. `PeerConnection.OnVideoTrackReceived` (`src/Client/WebRtc/PeerConnection.cs:495–505`)
umhüllt den eingehenden internen `byte[]` OHNE Kopie in die `EncodedFrame`. Ein SFU, der den Frame **asynchron**
an N andere Peers weiterreicht (`SendFrameAsync` awaited nach dem Callback), MUSS den Payload vorher kopieren
(`.ToArray()`), sonst liest er einen wiederverwendeten Depacketiser-Reorder-Buffer.
→ Konsequenz für unseren Router: Copy-on-receive vor Fan-out. **(Doku ist korrekt und warnt bereits;
optional die SFU-Forwarding-Implikation an `RemoteTrack.FrameReceived` explizit nennen.)**

### B2 — PLI wird nicht automatisch stromaufwärts propagiert

Ein SFU encodiert nicht. Braucht Downstream-Empfänger B einen Keyframe (Join/neuer Renderer), muss der
Router selbst verbrücken: eingehendes PLI feuert `IPeerConnection.VideoKeyFrameRequested` am Downstream-Peer
→ Router ruft `RequestVideoKeyFrameAsync(upstreamMid)` am Upstream-Peer. Keine SDK-Automatik — bewusst
(ADR-011: „the SDK stays a peer: it forwards media, it does not mix or transcode; the SFU/selection logic
lives in your app"). Throttle 500 ms ist eingebaut. **Kein Fix — Design.**

### B4 — `VideoKeyFrameRequested` trägt keine MID → keine gezielte Upstream-PLI im SFU

`IPeerConnection.VideoKeyFrameRequested` (`src/Client/WebRtc/IPeerConnection.cs:65–69`) ist parameterlos
(`event EventHandler?`). Wenn ein Downstream-Browser eine PLI für **einen bestimmten** ausgehenden Track
(einen von N geforwardeten Remote-Teilnehmern) sendet, erfährt der SFU-Router nicht, welche MID/welcher
Track betroffen ist — er kann die PLI also nicht gezielt an den richtigen Upstream-Peer weiterleiten und
muss ersatzweise Keyframes von ALLEN Upstreams anfordern (grob, aber korrekt; 500 ms-Throttle mildert es).
Gegenstück existiert bereits auf der Sende-Seite: `RequestVideoKeyFrameAsync(mid)` ist MID-fähig. Wunsch:
eine MID-tragende Überladung/Variante des Events (z. B. `event EventHandler<string>? VideoKeyFrameRequestedForMid`),
damit ein SFU die PLI 1:1 an den Quell-Peer routen kann. **Kein Bug — API-Lücke; SFU-Wunsch, kein Codefix nötig.**

### B3 — `FrameReceived`-Handler läuft synchron auf dem Receive-Loop-Thread

Die internen Frame-Events sind `Action<…>` (nicht `Func<Task>`) und werden single-consumer auf dem
Bundle-Receive-Loop gefeuert. Ein Fan-out-Handler darf NICHT blockieren/awaiten — Sends müssen
fire-and-forget bzw. über eine Queue/Channel entkoppelt werden. `SendFrameAsync` ist gegen gleichzeitiges
`DisposeAsync` via Drain-Gate gehärtet (wirft `ObjectDisposedException` nach Dispose). **Kein Fix — Design;
optional als SFU-Guidance dokumentieren.**

---

## Konsequenz fürs SFU-Design (Callora `SfuRoomMediaRouter`)

- **Join-Anytime via Renegotiation** (nicht Max-Slots) — dank A1 (Realität, nicht die Doku).
- Empfang pro Remote-Track über `TrackReceived`/`RemoteTrack.FrameReceived` (MID-getaggt, `EncodedFrame` mit RtpTimestamp/IsKeyFrame/Rid).
- Senden pro Downstream-Teilnehmer über `AddVideoTrack`/`AddAudioTrack` → `IVideoTrack/IAudioTrack.SendFrameAsync`, **Source-RtpTimestamp 1:1 durchreichen** (A/V-Sync bleibt erhalten).
- **Copy-on-receive** vor async Fan-out (B1), **manuelle PLI-Bridge** Downstream→Upstream (B2), **nicht-blockierender** Frame-Handler (B3).
