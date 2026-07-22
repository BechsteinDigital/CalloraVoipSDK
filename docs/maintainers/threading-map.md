# Threading- und Ownership-Karte

Konsolidierte Sicht auf alle Laufzeit-Threads/-Loops, Event-Thread-Verträge, Locks/Gates
und Dispose-Reihenfolgen. Die Verträge stehen verteilt als XML-Doku an den Klassen; dieses
Dokument ist die Landkarte dazu. Quelle: Tiefenanalyse 2026-07-22.

## 1. Laufzeit-Threads und -Loops

### SIP-Schicht (pro `VoipClient`/Transport-Runtime)

| Loop | Wo | Mechanik |
|---|---|---|
| UDP-Receive-Loop | `SipTransportRuntime` | `Task.Run`, gemeinsame Stop-CTS |
| TCP-/TLS-Accept-Loops | `SipTransportRuntime` | `Task.Run` je Listener |
| WS/WSS-Accept-Loop | `SipTransportRuntime` (`HttpListener`) | `Task.Run`; Upgrade nur mit `sip`-Subprotokoll (HARD-E6) |
| Receive-Loop je Stream-Verbindung | `SipStreamConnection` / `SipWebSocketConnection` | Framing + Idle-Timeout 5 min; **Dispose joint den Loop synchron** |
| Server-Transaktions-Timer | `ScheduledActionScheduler` | **ein** Worker-Thread + PriorityQueue; alle Timer G/H/J/L laufen hier — ein blockierender Callback verzögert alle SIP-Timer |
| Register-Refresh-Loop | `SipLineChannel` (je Line) | Task mit `Task.Delay`, Backoff |
| Session-Timer / 100rel-Retransmit / Subscription-Refresh | `SipSessionTimerManager`, `SipReliableProvisionalManager`, `SipCallSignalingSubscriptions` | `Task.Delay` + verkettete CTS |

**Wichtigster Vertrag:** `SipCallSignalingService.HandleInboundRequest` läuft **synchron
auf dem Receive-Thread** (Ingress-Validierung, Transaktions-Registrierung, 100 Trying).
Erst der Dispatch an eine bestehende Session läuft als Task. Blockierende Arbeit auf
diesem Pfad blockiert den gesamten Transport-Eingang — deshalb ist z. B. das synchrone
DNS in `SipLineChannel.IsSessionForThisLine` ein bekannter Befund.

### Medien-Schicht (pro Call bzw. pro Peer)

| Loop | Wo | Mechanik |
|---|---|---|
| RTP-Receive-Loop | `RtpSession` (einer je Socket) bzw. `BundledMediaTransport` | 1 Thread, gepoolter Buffer; Demux RFC 7983 (STUN/DTLS/RTCP/RTX/RTP) |
| Playout-Loop (nur Einzelstream) | `RtpCallMediaSession` | `PeriodicTimer`, Intervall = Paketdauer/4, geklemmt 2–10 ms; zieht aus dem Jitter-Buffer, PLC |
| RTCP-Reporter | `CallRtcpQualityMonitor` (SIP; 5-s-`PeriodicTimer`, eigener UDP-Socket im Non-Mux-Fall) bzw. `BundledRtcpReporter` (RFC-§6.3-Intervall) | SR/RR+SDES, RTT-Ableitung |
| DTLS-Handshake-Worker | `DtlsSrtpHandshaker` | `Task.Run`; BouncyCastle-Engine ist **blockierend**; Abbruch nur über `transport.Close()` |
| ICE-Consent-Loop | `IceMediaConsentSession` | ~5 s × [0.8, 1.2]; 30 s ohne Antwort ⇒ Consent-Lost |
| ICE-Nomination-Loop (nur Controlling) | `IceNominationDriver` | langlebige dynamische Check-Liste über den **geteilten Media-Socket** |
| TURN-Keepalives (bei Relay) | `TurnAllocationRefreshLoop` / `TurnPermissionRefreshLoop` / `TurnChannelRebindLoop` | je ½ Lifetime; Teardown via Refresh-0 |
| ICE-Auswahl (einmalig je Negotiation) | `CallMediaOrchestrator` | `Task.Run`, um den Signaling-Thread nicht zu blockieren |

**Single-Consumer-Annahmen:** Depacketiser-, DTMF-Reassembly- und Reorder-Zustände sind
bewusst unsynchronisiert — sie gehören exklusiv der einen Receive-Loop. Wer einen zweiten
Konsumenten anschließt, bricht den Vertrag.

### Audio-Schicht

| Thread | Wo |
|---|---|
| Capture-Callback | PortAudio-Callback (Linux) / `WaveInEvent` (Windows) — Echtzeit; Lock nur für kurze Snapshot-Reads, Sends fire-and-forget |
| Playback-Callback | PortAudio-Callback / `WaveOutEvent` + `BufferedWaveProvider` |
| Entkopplung RX ↔ Playback | `BoundedPlaybackBuffer` (bounded 50×20 ms, Drop-Oldest, HARD-F4) — netzgetakteter RX-Pfad und hardwaregetakteter Callback treffen sich nur hier |

### Recording/Playback (Application)

| Loop | Wo |
|---|---|
| Recording-Writer-Loop | `RecordingSession` (bounded Channel 512, Drop-Oldest) |
| Playback-Reader-Loop | `PlaybackSession` (Frame-Pacing; Pause via 25-ms-Polling) |
| Connector-Pump | `MediaConnection` (bounded Channel 256) |

## 2. Event-Thread-Verträge (wer feuert auf welchem Thread)

| Event | Thread | Vertrag |
|---|---|---|
| `CallStateChanged`, `IncomingCall`, `HoldStateChanged`, `TransferRequested` | SIP-Receive-Thread | Handler dürfen weder blockieren noch werfen; `TransferRequested` erwartet eine **synchrone** Accept-Entscheidung |
| `DtmfReceived` | SIP-Receive-Thread (INFO-Pfad) **oder** RTP-Receive-Loop (RFC-4733-Pfad) — zwei mögliche Threads | wie oben |
| `QualitySnapshotChanged` | RTCP-Monitor-Timer | wie oben |
| `IceConnectionStateChanged` | Consent-/ICE-Threads | wie oben |
| `MediaFrameReceived` (Taps) | Playout-Loop (Einzelstream) bzw. Receive-Loop (Bundle) | Hotpath: schnell, nicht werfen — werfende Subscriber werden isoliert |
| WebRTC `TrackReceived`/`FrameReceived`/`ConnectionStateChanged` | Bundle-Receive-Loop bzw. Signalisierungs-Sequenz | `EncodedFrame.Payload` ist **nur während des Callbacks gültig** |
| `TelemetryManager.EventPublished/MetricPublished/CdrPublished` | Thread des jeweiligen Emitters | nicht blockieren |

`VoipClient.OnIncomingCall` ist die entschärfte Convenience-Variante: Async-Handler laufen
fire-and-forget mit vollständigem Catch.

**Dispatch-Disziplin überall:** Handler-Delegat wird **innerhalb** des Locks gesnapshottet,
Invocation läuft **außerhalb**; Subscriber-Faults werden pro Delegat isoliert
(`GetInvocationList` + try/catch).

## 3. Synchronisations-Inventar (die wichtigsten Primitive)

| Primitive | Wo | Schützt |
|---|---|---|
| `_sync` (Monitor) + `_operationGate` (SemaphoreSlim) | `SipCallSession` | Zustand bzw. Operations-Serialisierung; das Gate wird vor INVITE-Transaktionen **freigegeben** (CANCEL-Deadlock-Vermeidung) |
| Atomare Snapshot-APIs | `ISipCallSessionContext.AdvertisedPublicContact` (HARD-C1), `ActiveInvite` (HARD-C2) | paarige Zustände — nie feldweise lesen |
| `_sendSync` + `_srtpProtectSync` | `RtpSession` | Seq/TS/SSRC-Konsistenz bzw. ROC-Ordnung |
| Ein Monitor pro SRTP-/SRTCP-Kontext | `SrtpContext`/`SrtcpContext` | serialisiert Protect/Unprotect; auf dem Bundle serialisiert **ein** Lock je Richtung alle Tracks |
| `SendDrainGate` | `WebRtcPeerConnection`, `BundledVideoTrack` | Send-vs-Dispose-Race (HARD-C6): Dispose drained laufende Sends |
| Copy-on-write-Arrays (`volatile`) | `MediaTapSet`, Frame-Taps | lockfreier Hotpath-Dispatch |
| Immutables Record hinter volatile-Referenz | `LearnedPublicContact` (N2), `IceNominatedTarget` | Torn-Read-freie Cross-Thread-Publikation |
| `Interlocked.Exchange`-Once-Guards | überall (`_disposed`, `_runtimeStarted`, `MediaActivity.HungUp`, Subscriptions) | Idempotenz |
| `ConcurrentDictionary` + `SemaphoreSlim`-Mutation-Gates | STUN-/TURN-Server-Registries, Connection-Pool | Registry-Mutation, Double-Checked-Create |
| Per-Encoding-`SemaphoreSlim` | `BundledVideoSendEncoding` | Frame-Atomarität beim Simulcast-Send |

Durchgängig: `ConfigureAwait(false)`; `TaskCompletionSource` mit
`RunContinuationsAsynchronously`; Fire-and-forget nur mit Fault-Beobachtung.

## 4. Socket-Ownership

| Socket | Owner | Mitnutzer |
|---|---|---|
| SIP UDP-Socket + Listener | `SipTransportRuntime` | — |
| Outbound-TCP/TLS/WS-Verbindungen | `SipOutboundConnectionPool` | §18.4-Retry entfernt stale Verbindungen |
| RTP-Socket je m-line (Einzelstream) | `RtpSession` | ICE-Probes (srflx/relay), DTLS-Attachment, Consent — alle über **denselben** Socket (deshalb: Gathering vor Start) |
| RTCP-Socket (nur Non-Mux) | `CallRtcpQualityMonitor` | — |
| Bundle-Socket (ein 5-Tupel) | `BundledMediaTransport` | ICE, DTLS, TURN-Kontrollverkehr (`TurnControlTransactor`), alle Tracks |
| Portreservierungs-Sockets | `SipCoreCallChannel` (vor SDP-Bau) | freigegeben nach `MediaParametersNegotiated` |
| TURN-Server-Relay-Sockets | `TurnServerAllocation` | Sweep gibt abgelaufene frei |
| WebRTC-Media-Socket | `WebRtcPeerConnection` (Early-Bind) | Übergabe an die Session (`_socketHandedOver`); Orphan-Socket wird im Dispose geschlossen |

## 5. Dispose-Reihenfolgen (verpflichtend)

Reihenfolgen sind im Code dokumentiert und teils testgesichert — beim Ändern nachziehen:

- **`VoipClient.StopRuntimeAsync` → `Dispose`:** alle Calls auflegen → alle Lines
  deregistrieren → Orchestratoren → Lines → Registration/Signaling nur bei Ownership
  (`_ownsX`-Flags: DI-injizierte Services nie doppelt disposen) → Transport →
  Audio-Gerät nur bei Ownership. Dispose atomar geclaimt (HARD-C4).
- **`RtpCallMediaSession`/`RtpSession`:** Sendestopp → DTLS close_notify → Socket →
  Key-Zeroing (`CryptographicOperations.ZeroMemory`).
- **`BundledMediaSession`:** ICE → Relay-Transition drainen → Channel-Rebind →
  Keepalives → RTCP-Reporter (sendet BYE) → DTLS → Video → Transport.
- **`DtlsMediaAttachment`:** close_notify **vor** Cancel → Handshake-Task awaiten →
  Kontexte disposen.
- **`WebRtcPeerConnection.DisposeAsync`:** Drain-Gate → Session → Orphan-Socket;
  `PeerConnection`-Wrapper untrackt auch im Fehlerfall (`finally`).
- **`WebRtcRecording.StopAsync`:** erst Tap detachen (danach garantiert kein Frame mehr),
  dann `sink.CompleteAsync` genau einmal.
- **Stream-Verbindungen:** Dispose joint den Receive-Loop **synchron** — kann blockieren,
  wenn ein Frame-Dispatch hängt (bekannter Befund).

## 6. Zeitquellen

Monotone `Stopwatch`-Ticks für TWCC und PLI-Throttle. **Wanduhr** (`DateTimeOffset.UtcNow`)
für Jitter-Buffer-Scheduling, RTT-Ableitung, SR-NTP und die WebRTC-Statistik-Raten —
im Bundle-Pfad injizierbar (`utcNow`-Funcs) für Tests. NTP-Sprünge verfälschen die
Wanduhr-Pfade (bekannter Befund); neue Zeitlogik bitte monoton bauen. Im SIP-Signaling
gibt es **keine** Zeitabstraktion (Register-Befund F003) — Soaks können dort nicht
zeitgerafft werden.
