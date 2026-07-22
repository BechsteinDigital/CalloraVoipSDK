# Kern-Abläufe (Fluss-Walkthroughs)

Die fünf Sequenzen, die man als Maintainer im Kopf haben muss. Jeder Schritt nennt die
Klassenkette und — wo relevant — den ausführenden Thread (Definitionen der Threads in
[`threading-map.md`](threading-map.md)). Quelle: Tiefenanalyse 2026-07-22; Stand der
Klassennamen: Branch-Basis `main`/4.6.0-preview.

---

## 1. Abgehender Anruf (SIP) — end-to-end

**Aufrufer-Thread bis zum INVITE, danach SIP-Receive-Thread.**

1. `VoipClient.DialAndWaitUntilConnectedAsync` → `SdkConvenienceOrchestrator` (TCS-Warten
   auf Connected/OnHold/Terminated, Timeout 30 s, optional Auto-Hangup) — oder direkt
   `line.DialAsync`.
2. `PhoneLine.DialAsync` (`src/Core/Domain/Lines/PhoneLine.cs`): Guard `Registered` +
   `_maxCalls` → `SipLineChannel.PrepareOutboundChannel` liefert den `SipCoreCallChannel`
   **ohne** INVITE → `CreateCall` baut das `Call`-Aggregat und bindet sofort die
   `CallChannelCallbacks` → `onCallCreated` hängt den `CallMediaOrchestrator` an
   `MediaParametersNegotiated` → `CallManager.Register` → `TransitionTo(Dialing)`.
3. `SipCoreCallChannel.StartOutboundDialAsync`: reserviert Medienports (UDP-Sockets, vor
   SDP-Bau!) → SDP-Offer über `ISdpNegotiator` → `SipCallSignalingService.InviteAsync`.
4. `SipCallSignalingService.InviteAsync` (`Ingress/`): Zielplanung
   (`SipInitialRequestRoutingPlanner`, Preloaded-Route-Set loose/strict) →
   DNS-Kandidaten (`SipDnsRouteResolver`: NAPTR → SRV → A/AAAA, gewichtete Ordnung
   CF-041) → pro Kandidat `SipCallSession.CreateOutbound` + `StartOutboundInviteAsync`.
   Das `_operationGate` wird **vor** der langlaufenden Transaktion freigegeben, damit
   CANCEL möglich bleibt.
5. `SipCallSessionTransactionService.SendInviteTransactionAsync`: Branch/CSeq atomar
   setzen (`SetActiveInvite`, HARD-C2) → `SipClientTransactionExecutor.ExecuteAsync`
   (Timer A/B, UDP-Retransmission, automatisches ACK für 3xx–6xx).
   - Provisionals (SIP-Receive-Thread): Dialog-Update im `SipDialogManager` (Forking),
     `Ringing`-Übergang, 100rel → In-Order-PRACK-Kette (CF-044).
   - 401/407: stärkste Challenge (`SipDigestChallengeSelector`), CSeq++, Authorization
     **pro Versuch neu** mit frischem nc (CF-042/CF-047), Schleife.
   - 422: Min-SE übernehmen, Retry (gedeckelt).
   - 2xx: Remote-Tag → `ClearActiveInvite` → ACK nach §13.2.2.4 über Dialog-Routing
     (CF-014) → Remote-SDP verhandeln → Session-Timer aktivieren → `Established`.
   - CANCEL kreuzt 200: `TryConsumeCancelledInvite` ⇒ sofortiges BYE. Verspätete
     Fork-2xx: `SipForkedInviteHandler` (ACK + BYE).
6. Zustand fließt zurück: `SipCoreCallChannel` → `CallChannelCallbacks.OnStateChange` →
   `Call.TransitionTo` (Handler-Snapshot unter Lock) → Events `CallStateChanged` auf dem
   **SIP-Receive-Thread**.
7. Nach SDP-Abschluss feuert der Channel `MediaParametersNegotiated` (einmalig; Republish
   nur bei echtem Rekey via Signaturvergleich) mit der Enricher-Kette
   ICE → SRTP → DTLS (K2) → weiter bei Ablauf 3.

## 2. Eingehender Anruf (inkl. 100rel-Answer)

**Eingang auf dem SIP-Receive-Thread; Session-Verarbeitung als Task.**

1. Transport-Loop → `SipWireProtocol.TryParseRequest` →
   `SipCallSignalingService.HandleInboundRequest` (synchron auf dem Receive-Thread!):
   Ingress-Validierung (`SipIngressRequestPolicy`: Pflichtheader, Loop-Erkennung über
   eigenes Branch-Präfix, Max-Forwards) → `SipServerTransactionEngine.RegisterInboundRequest`
   (Retransmission ⇒ letzte Antwort erneut; zentrale Via-Reflexion received=/rport=
   **vor** Zielauflösung, CF-040) → Merged-INVITE-Check (`SipMergedInviteTracker`) →
   sofort `100 Trying`.
2. Neue Call-ID ⇒ Inbound-`SipCallSession` (Ringing) + `IncomingInvite`-Event →
   `SipLineChannel.HandleIncomingInvite`: Line-Zuordnung über `TrunkInboundMatcher`
   (User-Match, DID-Whitelist, Peer-Trust) → neuer `SipCoreCallChannel` →
   `PhoneLine.HandleInbound`: `_maxCalls`-Check (Überlauf ⇒ beobachteter Hangup,
   HARD-E2) → `CreateCall` → `TransitionTo(Ringing)` → `Register` →
   `IncomingCall`-Event (aggregiert vom `PhoneLineManager`, weitergereicht als
   `VoipClient.IncomingCall`).
3. Konsument ruft `call.AcceptAsync()` → `SipCoreCallChannel.AnswerAsync`:
   SRTP-Policy-Gate (`SipCallChannelSrtpPolicyGuard`, Verstoß ⇒ 488) → SDP-Answer →
   `SipCallSession.AnswerAsync`:
   - Require-Validierung (unbekannte Option-Tags ⇒ 420),
   - falls INVITE `Require: 100rel`: 180 mit RSeq, UDP-Retransmit mit Backoff,
     PRACK-Wait (Timeout ⇒ 504 + Terminated),
   - Session-Timer-Validierung (400/422, RFC 4028),
   - 200 OK mit SDP → `Established`.
4. Channel publiziert `MediaParametersNegotiated` und gibt die
   Portreservierungs-Sockets frei → Ablauf 3.

Bereits bestehende Call-ID ⇒ Dispatch an `SipCallSessionInboundService` (als Task):
Dialog-Identitäts-Gate (CF-013, 481 bei Tag-Mismatch) → Handler je Methode
(BYE/re-INVITE/UPDATE/INFO/REFER/NOTIFY/PRACK/CANCEL …).

## 3. Media-Session-Aufbau (ICE → Keying → RTP)

**Start auf dem Thread des `MediaParametersNegotiated`-Events; mit ICE via `Task.Run`.**

1. `CallMediaOrchestrator.OnMediaParametersNegotiated`
   (`src/Core/Application/Media/CallMediaOrchestrator.cs`): ohne ICE synchron
   `SetUpMediaSession`; mit ICE `Task.Run` → `ResolveIceCandidatePairAsync`.
   ⚠ Bekanntes Race: terminiert der Call währenddessen, wird die Session trotzdem
   registriert und leakt bis zum Orchestrator-Dispose (P1-Befund #6 in MAINTAINING.md).
2. ICE (`CallIceAgent`): Gathering auf dem **Media-Socket** (host; srflx via
   `StunIceProbe`; relay via `TurnAllocationProbe`/`TurnIceRelayAllocator`) →
   Connectivity-Checks + reguläre Nominierung → Ergebnis als `with`-Klon der Parameter
   (nur Endpunkte ersetzt, HARD-R5) → ICE-Snapshot/State auf den Call.
3. `SetUpMediaSession`: Alt-Session abräumen (re-INVITE) →
   `RtpCallMediaSessionFactory.Create` → Verdrahtung: Inbound-Frames →
   `channel.DeliverInboundAudioFrame`; Send-Delegates (Audio/DTMF/Video) auf den Channel;
   Consent-Events → `SetIceConnectionState`; Video-Congestion/Keyframe → Call. Dann
   `StartSessionAsync` + `CallRtcpQualityMonitor`-Start.
4. Keying in der `RtpCallMediaSession`:
   - SDES: `SdesMediaCryptoContextFactory` baut die Kontexte aus den Enricher-Keys
     (fail-closed bei unparsbarem Material).
   - DTLS: `DtlsMediaAttachment` → `DtlsSrtpHandshaker` (BouncyCastle auf eigenem
     Worker-Thread, Fingerprint-Prüfung im Handshake) → Key-Export → 4(+2 RTX)
     SRTP/SRTCP-Kontexte installieren. Bis dahin gilt `RequireEncryptedMedia`:
     kein Klartext in beide Richtungen (K1).
5. Laufzeit-Loops danach: RTP-Receive-Loop (Demux STUN/DTLS/RTCP/RTX/RTP),
   Jitter-Buffer + Playout-Loop (PLC bis 3 Concealment-Frames), RTCP-Monitor
   (5-s-Reports, RTT → Jitter-Buffer-Rückkopplung), Consent-Loop (~5 s),
   TURN-Keepalives bei Relay.
6. Teardown: `CallManager` entfernt den Call bei `Terminated`;
   `CallMediaOrchestrator.OnCallStateChanged` → `TeardownMediaAsync` (fire-and-forget):
   Metrics-Log → vollständiges Unwiring (`ActiveMediaEntry` hält alle Handler-Referenzen)
   → Monitor + Session `DisposeAsync`.

## 4. Registrierung, NAT-Lernen, Reconnect

**Eigener Register-Loop-Task pro Line.**

1. `PhoneLineManager.Register(account)` → Factory baut `SipLineChannel` + `PhoneLine` →
   `line.StartRegistration()` verdrahtet `TransitionTo` + Reconnect-Callbacks.
2. `SipLineChannel`-Loop: Request mit **persistierter** Call-ID/CSeq (§10.2.4) →
   `SipRegistrationService.ExecuteRegisterAsync` (Kandidaten-Iteration, Digest-/Stale-/
   423-Min-Expires-/Redirect-Retries, alle gedeckelt) → Effective-Expires-Auswahl
   (eigenes Binding > Expires-Header > längstes Binding > Fallback).
3. NAT-Lernen (N2): `ObservedPublicHost/Port` aus received=/rport= der Registrar-Via →
   `NatPublicContactState.ApplyObserved` (pure, idempotent) → bei echter Änderung
   sofortige korrigierende Re-Registrierung (gedeckelt über
   `MaxCorrectiveReregistrations`). Manueller Override (N1, `PublicSipHost/Port`)
   gewinnt immer. Publikation als immutables `LearnedPublicContact` hinter
   volatile-Referenz — Leser (Inbound-INVITE-Thread) sehen nie ein zerrissenes Paar.
4. `Registered` → Refresh-Delay (Ratio 0.8, MinRefreshInterval 15 s) → Schleife.
   Fehler: 401/403 ⇒ `Failed` (permanent); sonst Exponential-Backoff 2–60 s,
   `Reconnecting`-Events, nach MaxRetries `ReconnectFailed`.
5. `UnregisterAsync`: Loop canceln + **awaited** REGISTER Expires:0 mit derselben
   Binding-Identität (HARD-E1). Der Dispose-Pfad nutzt dagegen das synchrone
   Best-Effort-`StopRegistration`.

## 5. WebRTC-Verbindungsaufbau (`ConnectAsync`)

**Single-Caller-Sequenz (HARD-C6-Vertrag); Medien danach auf der Bundle-Receive-Loop.**

1. `WebRtcClient.CreatePeer()`: DTLS-Identität (gepinnt oder ephemer P-256, HARD-E7),
   `WebRtcPeerOptions` (Codecs, ICE-ufrag/pwd, trickle), STUN-/TURN-Probes nur bei
   konfigurierten Servern → Core-`WebRtcPeerConnection` → Wrapper `PeerConnection` →
   Tracking im `PeerConnectionManager`.
2. `WebRtcPeerConnectionExtensions.ConnectAsync(peer, signalling, role)`:
   „established"-TCS wird **vor** jedem Schritt gearmt (kein verpasster Übergang).
   - Offerer: `CreateOffer` → `SendDescriptionAsync` → `ReceiveDescriptionAsync` →
     `SetRemoteDescriptionAsync(answer)`.
   - Answerer: umgekehrt; `SetRemoteDescriptionAsync(offer)` liefert die Answer.
   - Der Host-Kandidat reitet im SDP (Early-Bind: `EnsureLocalMediaEndPoint` bindet den
     Media-Socket vor dem Offer).
3. Trickle (nur bei `IWebRtcTrickleSignaling`): parallel `PumpRemoteCandidatesAsync`
   (bis null = end-of-candidates); lokal `LocalIceCandidateDiscovered` abonnieren →
   `GatherCandidatesAsync` (**muss vor `StartAsync`** — teilt den Media-Socket; srflx via
   STUN, relay via TURN-Probe) → gepufferte Kandidaten senden → `SendEndOfCandidatesAsync`.
4. `SetRemoteDescriptionAsync` materialisiert Remote-Tracks sofort (W3C-`ontrack`) und
   `WebRtcSessionFactory.TryCreate` leitet die `BundledMediaSession` ab (MID/RID-Ids,
   DTLS-Rolle aus beiden `a=setup`, Remote-Endpoint via `WebRtcRemoteEndPoint` —
   Single-Candidate, kein voller ICE-FSM; SSRCs kollisionfrei zufällig).
5. `StartAsync`: Bundle-Receive-Loop, ICE-Consent-Loop, DTLS-Handshake → bei
   installierten Keys `Connected` → TCS erfüllt. `finally`: Events abhängen, CTS
   canceln, Kandidaten-Pump awaiten.
   ⚠ Honoriert die App-Implementierung von `ReceiveCandidateAsync` das Token nicht,
   hängt `ConnectAsync` (P2-Befund).
6. Medien: `SendAudioAsync`/`SendVideoFrameAsync` über Drain-Gate (HARD-C6);
   Inbound → `RemoteTrack.FrameReceived`; Taps (`MediaTapSet`, copy-on-write) sehen
   beide Richtungen; `WebRtcRecorder` hängt sich als Tap ein
   (Stop: erst Tap detachen, dann `sink.CompleteAsync` genau einmal).
