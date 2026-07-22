# Vollständige Quelltext-Tiefenanalyse — CalloraVoipSdk

**Stand:** 2026-07-22, Branch-Basis `main` (`10b1233`), Version 4.6.0-preview.1
**Umfang:** Sämtliche 1091 C#-Dateien des Repositories wurden gelesen — `src/` (≈750 Quelldateien) vollständig Datei für Datei bis in jede Klasse, dazu Tests (322 Dateien, Harness-Klassen vollständig), Performance-Projekte, Beispiele, Build/CI und Dokumentationskonfiguration.
**Methode:** Sieben parallele Tiefenanalysen je Subsystem (SIP, RTP/SRTP/DTLS, STUN/TURN/ICE, SDP/Media/Common, Core-Application/Domain, Client/Audio/WebRTC-Fassade, Tests/CI/Beispiele), anschließend Konsolidierung. Jeder Teilbericht enthält einen vollständigen Klassenkatalog seines Bereichs mit Datei- und Zeilenangaben.

---

## 1. Gesamtüberblick

CalloraVoipSdk ist ein kommerzielles .NET-VoIP-SDK (net8.0/net9.0/net10.0) mit einem ungewöhnlich vollständigen, selbst implementierten Protokoll-Stack — es gibt **keine** externe SIP-/RTP-/STUN-Bibliothek; einzige Krypto-/Codec-Fremdanteile sind BouncyCastle (DTLS, SHA-512/256), Concentus (Opus), NAudio (G.711/G.722, Windows-Audio), PortAudioSharp2 (Linux-Audio) und DnsClient.NET (RFC-3263-Routing).

Die Solution gliedert sich in fünf Paket-Projekte plus Tests/Perf/Beispiele:

| Projekt | Inhalt |
|---|---|
| `CalloraVoipSdk.Core` (649 Dateien) | Kompletter Protokoll-Stack: SIP (UAC+UAS, 5 Transporte), SDP/Offer-Answer, RTP/RTCP/Jitter/RTX/TWCC, SRTP/SRTCP, DTLS-SRTP, STUN/TURN/ICE (Client **und** Server), Domain-/Application-Schicht (DDD, Ports & Adapters) |
| `CalloraVoipSdk.Client` (92 Dateien) | Öffentliche Fassaden: `VoipClient` (SIP) und `WebRtcClient` (WebRTC, 4.6-Preview), Manager-Schicht, Modul-System, DI/Hosting, STUN-/TURN-Server-Hosting |
| `CalloraVoipSdk.Audio.*` (9 Dateien) | Audio-Backends: Abstractions, Headless, Linux (PortAudio), Windows (NAudio) — per Reflection nachladbar |
| `CalloraVoipSdk` | Meta-Paket (Client + Audio.Abstractions) |

**Architekturstil:** Hexagonal (Ports & Adapters) mit DDD-Anleihen — Domain (`Call`-/`PhoneLine`-Aggregate, Zustandsautomaten, Domain-Events) hängt von nichts ab; Application definiert Ports; Infrastructure implementiert sie; der Client verdrahtet alles per „resolve-or-default" aus dem DI-Container. Die Schichtregeln werden durch eigene **Architektur-Tests mit Nur-Schrumpfen-Baselines** maschinell erzwungen.

**Durchgängige Qualitätsmerkmale (alle sieben Teilanalysen unabhängig bestätigt):**

- Außergewöhnlich hohe RFC-Kommentardichte mit Paragraphenangaben (RFC 3261/3262/3263/3264/3311/3515/3550/3711/4028/4568/4733/5389/5626/5764/5766/6062/6665/7616/7675/8285/8445/8656/8829/8838/8843/8853 u. v. m.); bewusste Abweichungen sind als „DECISION"/„Limitation"/„follow-up" markiert.
- Internes Findings-Nummernsystem (CF-xxx, HARD-xxx, F00x) markiert behobene Bugs und bekannte Grenzen direkt im Code — nachvollziehbare Regressionshistorie.
- Konsequentes Fail-Closed-Security-Design: kein Klartext-Downgrade bei SRTP/DTLS, verify-then-decrypt, konstantzeitige Vergleiche, Key-Zeroing, DoS-Limits auf jeder Wire-Ebene, Secret-Redaction in Trace-Logs.
- Diszipliniertes Nebenläufigkeitsmodell: Handler-Snapshots unter Lock, atomare Snapshot-APIs gegen Torn Reads, idempotente Dispose-Pfade, bounded Buffer mit Drop-Metriken, fault-isolierte Event-Dispatches.
- Es existieren **keine** TODO/FIXME/HACK-Marker im Produktcode; offene Punkte sind als strukturierte Follow-up-Kommentare dokumentiert.

**Reifegrad nach Subsystem (Kurzfassung):** SIP-Stack und STUN/TURN/ICE sind am reifsten; RTP-Einzelstream-Pfad (SIP-Calls) ist vollständig, der BUNDLE-/WebRTC-Pfad transportseitig komplett, aber im Reparatur-/Feedback-Bereich (NACK/RTX/PLI/TWCC auf Bundle) noch deutlich dahinter; die WebRTC-Fassade ist erklärter Preview-Stand (nicht browser-validiert, kein SCTP, kein TCP/TLS-TURN); Audio-Plattformschicht ist funktional, aber dünn getestet und mit Plattform-Paritätslücken.

## 2. Konsolidierte Top-Befunde

Die vollständigen Befundlisten mit Datei:Zeile stehen in den Teilberichten (Kapitel „Qualitätsbefunde"). Nach praktischer Relevanz konsolidiert:

### Priorität 1 — Interop-/Stabilitätsrisiken in realen Deployments

1. **SIP: Fehlendes Re-ACK auf retransmittierte 2xx** (RFC 3261 §13.2.2.4) — geht das erste ACK verloren, wird das wiederholte 200 OK nie erneut geACKt; der Gegenstack retransmittiert bis Timeout und baut den Call ggf. ab (`SipCallSessionTransactionService.cs:200`, `SipClientTransactionExecutor.cs:371-375`, `SipForkedInviteHandler.cs:181`).
2. **SIP: Kein Digest-Retry auf Refresh-Pfaden** — Session-Timer-UPDATE (`SipCallSessionTransactionService.cs:479-519` → BYE nach erstem Refresh-Intervall hinter auth-fordernden Proxies) und SUBSCRIBE-Refresh (`SipCallSignalingSubscriptions.cs:259-305`, zusätzlich Expires-Clamp ≥60 s über der Server-Lease).
3. **SIP: Malformiertes `+sip.instance`** — Parametername fälschlich gequotet (`SipRegistrationService.cs:567`); strenge Registrare lehnen den Contact ab (RFC 5626).
4. **SRTP: SRTCP-Auth-Tag bei `*_HMAC_SHA1_32`-Suiten nur 4 statt 10 Bytes** — RFC-Verstoß (RFC 4568 §6.2); Interop-Bruch mit libsrtp-basierten Peers, sobald die _32-Suite ausgehandelt wird (`SrtcpContext.cs:43-45`).
5. **TURN-Server: MESSAGE-INTEGRITY-Zwang auf Send-Indications** — RFC 5766/8656 §10 verbietet Auth auf Indications; RFC-konforme Fremd-Clients werden abgelehnt (`TurnServer.cs:1751`, `TurnServerRequestAuthenticator.cs:2520-2551`). Intern konsistent, extern ein Interop-Bruch.
6. **TURN-Server: Relay-Adresse fällt auf Loopback zurück** — ohne konfigurierbare öffentliche Relay-Adresse annonciert ein real deployter Server ggf. `127.0.0.1` als Relay (`TurnAllocateRequestHandler.cs:234-249`).
7. **Core: Transfer kann in `Transferring` hängenbleiben** — wirft der Kanal während Blind-/Attended-Transfer, fehlt das try/finally für die Rücktransition; nur Hangup führt heraus (`Call.cs:163-185`). `AttendedTransferAsync` hat zudem keinen State-Guard.
8. **Core: ICE-Race mit Media-Session-Leak** — terminiert ein Call während der asynchronen ICE-Auswahl, registriert der ICE-Task anschließend eine Session (inkl. RTP-Socket/RTCP-Loops) für einen toten Call; sie lebt bis zum Orchestrator-Dispose (`CallMediaOrchestrator.cs:140-152` vs. `286-304`).
9. **Audio: G.722 wird zustandslos transkodiert** — pro Frame ein frischer `G722CodecState` statt fortlaufendem ADPCM-Prädiktor-Zustand → hörbare Artefakte in Aufnahme/Wiedergabe (`PcmG722Codec.cs:19,38`).
10. **RTP: 8-KiB-Kernel-Empfangspuffer** auf allen Media-Sockets (`RtpSession.cs:90,160`, `BundledMediaTransport.cs:35,103`, `WebRtcPeerConnection.cs:773`) — für Video-Bitraten deutlich zu klein, Kernel-Drops unter Last wahrscheinlich.

### Priorität 2 — Randfälle, Härtung, RFC-Formalia

- Plain-RTP-Symmetric-Latch re-latcht auf jedes valide Paket (Media-Hijack-Fenster ohne SRTP; `RtpSession.cs:645-651`); SSRC/Seq-Zufall nicht kryptographisch.
- SDP: rtcp-mux in der Answer auch ohne Offer (`SdpOfferAnswerNegotiator.cs:199`, RFC 5761-Verstoß mit potenziellem RTCP-Verlust); `b=TIAS` wird als kbps interpretiert und als `b=AS` re-serialisiert; fehlende `c=`-Zeile defaultet still auf Loopback statt Ablehnung.
- ICE: Rollenkonflikt-Auflösung mit `>=` statt striktem `>` (formale RFC-8445-Abweichung); zwei parallele ICE-Implementierungen (`IceConnectivityScheduler` vs. `IceNominationDriver`) — Redundanz-/Verwirrungsquelle.
- `VoipClient`-Konstruktor räumt bei mittigem Fehlschlag nicht auf (Socket-/Listener-Leak); `WebRtcOptions` ohne Startup-Validator (asymmetrisch zur SIP-Seite); nicht thread-sichere Event-Accessors in `PeerConnection`; `DateTime.UtcNow` statt monotoner Uhr in Stats und Jitter-Buffer-Scheduling.
- Windows-/Linux-Audiogeräte: erhebliche Code-Duplikation, invertierte Drop-Politik (Windows verwirft Neuestes statt Ältestes), totes Konfigfeld `FramesPerBuffer` (Linux), fehlende Playback-Metriken (Windows), Aliasing durch Nearest-Neighbor-Resampling, fire-and-forget-Sends auf dem Capture-Pfad.
- MP3-Passthrough scheitert an ID3v2-Tags; Recording-Verschlüsselung lädt ganze Dateien in den RAM; ffmpeg-Prozesse werden bei Cancellation nicht gekillt.

### Priorität 3 — Test-/Build-Infrastruktur

- Perf-Regression-Gate existiert, wird aber **von keinem CI-Workflow aufgerufen**; Baseline veraltet (net8/2026-04); `Conferencing.Performance` referenziert ein nicht existentes Projekt und baut nicht.
- Interop-Abdeckung minimal: genau **ein** echter Test (REGISTER gegen Asterisk); ohne Docker skippen Interop-Tests still grün.
- Release-Gate (`packages.yml:50`) testet ungefiltert inkl. Long-Soaks — langsam und flaky-anfällig; ArchitectureTests laufen in CI doppelt; `EngineeringRulesTests` scannt `samples/` statt `examples/`; Coverage ohne Schwellwert-Gate.
- `InternalsVisibleTo` auf nicht existente, unsignierte Assembly-Namen (`AssemblyInfo.cs:3-6`) — unnötig weite Internals-Öffnung.
- Bekannte offene Register-Befunde: F002 (Late-Drops als `PacketsUnrecoverableLoss` fehlklassifiziert, Soak-Test geskippt), F003 (keine Zeit-Abstraktion im Signaling), F004 (RTT auf L2 statischer Hint).

## 3. Gesamturteil

Eine für den Umfang (kompletter Eigenbau-Stack von Wire-Byte bis Fassade) außergewöhnlich reife, disziplinierte Codebasis: klare Schichtung mit maschinell erzwungenen Regeln, dokumentierte RFC-Treue inklusive markierter Abweichungen, Fail-Closed-Security, selbstkritisches Audit-Register und eine Test-Engineering-Kultur (L0–L4, Soak-OLS-Trends, Drift-Guards), die deutlich über Branchenüblichem liegt. Die substanziellen Schwächen konzentrieren sich auf (a) eine Handvoll konkreter Interop-Bugs in Randpfaden (Re-ACK, Refresh-Auth, `+sip.instance`, SRTCP-Tag, TURN-Indication-Auth), (b) Lebenszyklus-Races in der Orchestrierung (Transfer-Zustand, ICE-Teardown, Konstruktor-Rollback) und (c) die noch unfertige Peripherie (Bundle-Feedback-Pfad, WebRTC-Preview-Lücken, Audio-Plattformparität, nicht verdrahtetes Perf-Gate, minimale Interop-Tests).

---

*Es folgen die sieben vollständigen Teilberichte mit Klassenkatalogen bis in die letzte Klasse.*



---

# Teil 1 — SIP-Stack (`src/Core/Infrastructure/Sip/`, 129 Dateien)


**Umfang:** 129 C#-Dateien, ~20.750 Zeilen, vollständig gelesen. Alle Pfadangaben sind relativ zu `/home/user/callora-voip-sdk/src/Core/Infrastructure/Sip/` (im Befundteil absolut mit Zeilennummern).

---

## 1. Überblick & Verantwortung

Das Subsystem ist der komplette **SIP-User-Agent-Stack** (UAC+UAS) des SDK: Wire-Parsing/-Serialisierung, Multi-Transport-I/O (UDP/TCP/TLS/WS/WSS), Client- und Server-Transaktionen mit RFC-Timern, Dialog-/Session-Verwaltung (INVITE-Lebenszyklus, Hold, Transfer, DTMF, Subscriptions), Registrierung, Digest-Authentifizierung, DNS-Routing sowie Adapter zur Domänenschicht (`ICallChannel`/`ILineChannel`) und Telemetrie. Es ist bewusst ein reiner Endpunkt-Stack (kein Proxy) und „transport-only" für Medien: SDP-Verhandlung wird über Delegates (`SipSessionSdpProvider`) bzw. den `ISdpNegotiator` der Nachbarschicht angebunden; Medienframes sind opake Bytes.

**Erkennbare RFC-Abdeckung (explizit im Code referenziert):**

| RFC | Thema | Wo |
|---|---|---|
| 3261 | Kernprotokoll: Parsing (§7), UAC/UAS (§8), CANCEL (§9), REGISTER (§10), Dialoge (§12), INVITE (§13), Transaktionen (§17), Transport (§18), URIs (§19) | Wire, Transactions, Signaling |
| 3262 | 100rel/PRACK/RSeq/RAck | Reliability, TransactionService, InboundService |
| 3263 / 2782 | DNS NAPTR→SRV→A/AAAA, gewichtete SRV-Auswahl | Routing |
| 3264 | Offer/Answer, re-INVITE-Rekey | InboundService, SipCoreCallChannel |
| 3311 | UPDATE (Session-Refresh, Target-Refresh) | TransactionService, InboundService |
| 3323/3325 | Privacy / P-Asserted-/P-Preferred-Identity | HeaderService, Identity |
| 3326 | Reason-Header | SipReasonHeader |
| 3327/3608 | Path / Service-Route | SipRegistrationService |
| 3515 / 4488 / 3892 | REFER, norefersub, Referred-By | InboundService, TransactionService |
| 3581 | rport / received-Reflexion | SipProtocol, ServerTransactionEngine |
| 3891 / 5589 | Replaces / Attended Transfer | SipReplacesHeaderValue, AttendedTransferReferTo |
| 3966 | tel-URI → SIP-URI | SipProtocol |
| 4028 | Session-Timer (Session-Expires/Min-SE/422) | SipSessionTimerPolicy/-Manager |
| 5626 | Keepalive (CRLF-Ping/Pong), +sip.instance/ob/reg-id | Framer, StreamConnection, RegistrationService |
| 5621 | Body-Handling (handling=optional) | SipContentPolicy |
| 5763/4145/4568 | DTLS-SRTP-/SDES-Signalisierung | Adapters (Enricher) |
| 5806 | Diversion | SipCallSessionUtilities |
| 5922 | TLS-SIP-Domain-Validierung (SAN) | SipTransportRuntime |
| 6665 (3265-Nachfolger) | SUBSCRIBE/NOTIFY out-of-dialog + in-dialog | Subscriptions, Ingress |
| 7118 | SIP over WebSocket (Subprotokoll „sip") | Transport |
| 7616 / 8760 | Digest (SHA-256, SHA-512/256, auth-int, nc-Kopplung) | Authentication |
| 8445 | ICE-Rollen (controlling/controlled) | Adapters |

Auffällig ist ein internes Findings-Nummernsystem (CF-013, CF-014, CF-040…CF-047, HARD-C1/C2/C3, HARD-E6, HARD-R7, N1/N2), das dokumentierte Bugfixes/Compliance-Korrekturen im Code markiert — ein Zeichen für systematisch nachgezogene RFC-Konformität.

---

## 2. Architektur & Schichtung

```
                             (Domain/Application: ICallChannel, ILineChannel, ICallIceAgent, ISdpNegotiator)
                                              ▲
  Adapters ────────────────────────────────────┘   SipLineChannel (Registrierung/Leitung), SipCoreCallChannel (Call-Leg),
                                                   Media-Parameter-Enricher (ICE/SRTP/DTLS), Notifier/FrameTaps
       ▲
  Signaling                                        Ingress (SipCallSignalingService = zentraler Dispatcher),
   ├─ Contracts   (ISipCallSession, ISipCallSessionContext, DTOs)
   ├─ Dialogs     (SipCallSession + Header/Transaction/Inbound-Services, DialogManager, In-Dialog-Routing)
   ├─ Registration, Reliability (100rel), SessionTimers (4028), Subscriptions (6665), Formatting, Identity, Events
       ▲
  Transactions                                     SipClientTransactionExecutor (UAC-Timer A/B/E/F, D/K-Absorption),
                                                   Server/SipServerTransactionEngine (Retransmit G/H, J/L, ACK-Matching)
       ▲
  Transport                                        SipTransportRuntime (UDP/TCP/TLS/WS/WSS-Listener + Sende-Multiplexer),
                                                   SipOutboundConnectionPool, SipStreamConnection, SipWebSocketConnection,
                                                   SipWireStreamFramer, SipWireTraceLogger
       ▲
  Wire                                             SipWireProtocol (ISipWireCodec), SipRequest/SipResponse,
                                                   SipProtocol (Header-/URI-Helfer), HeaderRowRules/-ValueStorage
  Querschnitt: Routing (RFC 3263-DNS), Authentication (Digest), Observability (Telemetrie-Sinks)
```

**Zentrale Muster:**

- **Codec-/Port-Abstraktion:** `ISipWireCodec`, `ISipTransportRuntime`, `ISipTransportFactory`, `ISipRouteResolver`, `ISipDigestAuthenticator`, `ISipClientTransactionExecutor`, `ISipServerTransactionEngine` — durchgängig DI-freundliche Interfaces mit Default-Implementierungen.
- **Publisher/Subscriber (Sink):** Transport publiziert geparste Nachrichten über `SubscribeRequests/SubscribeResponses` (Rückgabe `IDisposable` zum Abmelden); Telemetrie über `ISipTelemetrySink` (Null-/InMemory-Implementierung).
- **State Machine:** `SipDialogState` (Idle→Inviting→Ringing→Established⇄OnHold→Terminated) mit `TransitionTo` als einzigem Übergangspunkt (Terminated ist absorbierend); `SipServerTransactionState` als Timer-getriebene Server-Transaktions-Zustandsmaschine; Client-Transaktion als async „inline state machine" (Retransmissionsschleife + TCS-Signale).
- **Fassade + Kontext-Adapter:** `SipCallSession` ist die Fassade; die Teilbelange (Header-Bau, Transaktionen, Inbound-Handling) leben in eigenen Services, die über das schmale `ISipCallSessionContext` (implementiert von `SipCallSessionContextAdapter`) auf den Session-Zustand zugreifen — inkl. dokumentierter atomarer Snapshot-APIs (`ActiveInvite`, `AdvertisedPublicContact`) gegen Torn Reads.
- **Strategy/Policy-Objekte:** statische Policy-Klassen (`SipIngressRequestPolicy`, `SipSessionTimerPolicy`, `SipRequireOptionPolicy`, `SipContentPolicy`, `NatPublicContactState`, `TrunkInboundMatcher`) kapseln reine Entscheidungslogik, meist als Pure Functions — sehr testfreundlich.
- **Provider-Delegates:** `SipSessionSdpProvider` entkoppelt SIP-Modul von SDP-Namespace über vier `Func<>`-Delegates.
- **Enricher-Kette (funktional):** `CallMediaParametersIceEnricher` → `SrtpEnricher` → `DtlsEnricher` klonen `CallMediaParameters` immutabel in fester Reihenfolge (Invariante im Code dokumentiert).

---

## 3. Klassenkatalog

### 3.1 Wire/ (8 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipWireCodec` (Interface) | `Wire/ISipWireCodec.cs` | Vertrag für Parse/Serialize von Request/Response aus/zu Bytes; ermöglicht Codec-Austausch per DI. |
| `SipHeaderNames` (static) | `Wire/SipHeaderNames.cs` | Kanonisierung inkl. Kompaktformen (v→Via, f→From, … o→Event, r→Refer-To). |
| `SipHeaderRowRules` (static) | `Wire/SipHeaderRowRules.cs` | Regeln nach RFC 3261 §7.3: kommakombinierbare vs. nicht kombinierbare Header (Auth-Header, Content-Length), Request-/Response-only-Filterlisten (Expires bewusst ausgenommen, damit es Response-Parsing überlebt), Contact-`*`-Sonderfall. |
| `SipHeaderValueStorage` (static) | `Wire/SipHeaderValueStorage.cs` | Speichert wiederholte, nicht kombinierbare Headerzeilen als `\n`-separierten String; `AppendRow`/`SplitRows`/`FirstRow`. Basis des „Mehrzeilen im Dictionary"-Modells. |
| `SipProtocol` (static, 879 Z.) | `Wire/SipProtocol.cs` | Werkzeugkasten: Branch/Tag/Call-ID-Generierung (lokales Branch-Präfix `z9hG4bK-vsdk-<token>-` für Loop-Erkennung), Statusklassen, UAC-Status-Normalisierung (§8.1.3.2), Via-Parsing (branch, sent-by, received/rport), Via-Reflexion (§18.2.1/RFC 3581, idempotent, CF-040), UDP-Response-Ziel (§18.2.2), Tag-Extraktion mit Quote-/Bracket-Scanner (CF-046), CSeq-Parsing, SIP-URI-Parsing/-Escaping (§19.1.2), vollständiger URI-Vergleich (§19.1.4) inkl. user=phone-Normalisierung, tel→sip-Konvertierung (§19.1.6). |
| `SipRequest` (sealed) | `Wire/SipRequest.cs` | Immutables Request-Modell (Method, RequestUri, Header-Map case-insensitive, Body); `Header()`/`HeaderValues()` mit Kanonisierung und Request-Anwendbarkeitsfilter. |
| `SipResponse` (sealed) | `Wire/SipResponse.cs` | Spiegelbild für Responses (StatusCode, ReasonPhrase) mit Response-Filter. |
| `SipUriComponents` (record struct) | `Wire/SipUriComponents.cs` | Zerlegte URI (Scheme, User, Host, Port, Params, Headers) für den §19.1.4-Vergleich. |
| `SipWireProtocol` (sealed) | `Wire/SipWireProtocol.cs` | Der Codec: 64-KiB-Limit (§26.1.5-DoS-Guard), CRLFCRLF- und Bare-LF-Terminator (§7.5), Header-Folding, Kompaktnamen, Content-Length-Konsistenz über Zeilen, Body-Constraints (§7.4: Content-Type-Pflicht bei Body, kein chunked, Content-Encoding nur mit Body), Startzeilen-Validierung (§7.1/§7.2, Reason-Phrase-Control-Char-Schutz CF-067), CR/LF-Injection-Schutz bei Serialisierung. |

### 3.2 Transport/ (12 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipTransportFactory` | `Transport/ISipTransportFactory.cs` | Erzeugt `ISipTransportRuntime` mit TLS-Konfiguration und Default-Transport. |
| `SipTransportFactory` | `Transport/SipTransportFactory.cs` | Default-Factory; warnt, wenn TLS konfiguriert, aber kein Zertifikat ladbar (TLS-Listener bleibt aus). |
| `ISipTransportRuntime` | `Transport/ISipTransportRuntime.cs` | Transport-Abstraktion: Send Request/Response (mit/ohne explizitem Transport), Subscribe, Endpoint-Auflösung; Default-Interface-Methode `ResolveRemoteRouteCandidatesAsync` als Ein-Kandidat-Fallback. |
| `SipTransportProtocol` (enum) | `Transport/SipTransportProtocol.cs` | Udp/Tcp/Tls/Ws/Wss. |
| `SipTransportRuntime` (sealed, 882 Z.) | `Transport/SipTransportRuntime.cs` | Herzstück: bindet UDP-Socket + TCP-Listener (ephemer, `IPAddress.Any`), optional TLS-Listener (bei Zertifikat) und WS/WSS-`HttpListener`; fünf Accept-/Receive-Loops als `Task.Run`; Inbound-Payload → Codec → Dispatch an Handler-Snapshots; Sende-Multiplexer nach Transport (UDP direkt, TCP/TLS/WS über Pool); RFC 3261 §18.1.1 MTU-Eskalation (UDP>1300 B → TCP mit Via-Umschreibung); Transport-Hints und TLS-Host-Map (SNI/Zertifikatname per Domain statt IP); RFC 5922-SAN-Validierung; WS-Upgrade nur mit „sip"-Subprotokoll (RFC 7118, HARD-E6). |
| `SipTransportRuntimeUtilities` (static) | `Transport/SipTransportRuntimeUtilities.cs` | Ephemere Portallokation (TcpListener-Probe), WS-URI-Bau, Wildcard→Loopback-Normalisierung, Endpoint-Keys, Via-UDP→TCP-Regex, TLS-Zielhost-Auswahl, Outbound-TLS-Handshake (TLS 1.2/1.3), Subprotokoll-Auswahl. |
| `SipOutboundConnectionPool` (sealed) | `Transport/SipOutboundConnectionPool.cs` | Pool ausgehender TCP/TLS- und WS/WSS-Verbindungen (`ConcurrentDictionary` + `SemaphoreSlim`-Gate, Double-Checked-Create); RFC 3261 §18.4: bei Sendefehler stale Verbindung entfernen und einmal auf frischer Verbindung wiederholen (nur Stream-Pfad). |
| `SipStreamConnection` (sealed) | `Transport/SipStreamConnection.cs` | Eine TCP/TLS-Verbindung: Framing-Receive-Loop mit Idle-Timeout (5 min), serialisierter Sendepfad (`_sendGate`), RFC 5626 §4.4.1 CRLF-Keepalive-Pong; Dispose joint den Loop synchron. |
| `SipWebSocketConnection` (sealed) | `Transport/SipWebSocketConnection.cs` | Eine WS/WSS-Verbindung: Message-Aggregation mit Größenlimit (Framer-Limits als Memory-DoS-Kappe), Text-Frames, Idle-Timeout, Close-Handling. |
| `SipWireStreamFramer` (sealed) | `Transport/SipWireStreamFramer.cs` | Inkrementeller Stream-Framer: CRLFCRLF + Content-Length-Pflicht (Stream-Transport), Header-/Body-Limits (64 KiB/256 KiB) mit Exception→Verbindungsabbau, führendes CRLF-Trimming inkl. Keepalive-Ping-Flag, chunked verboten. |
| `SipWireTraceLogger` (static) | `Transport/SipWireTraceLogger.cs` | Trace-Wire-Logging mit `IsEnabled`-Guards; **redigiert** SDES-Keys (`inline:`) und `a=ice-pwd:` aus SDP, bevor sie ins Log gelangen. |

### 3.3 Transactions/ (8 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipClientTransactionExecutor` | `Transactions/ISipClientTransactionExecutor.cs` | Führt eine Client-Transaktion aus, wartet auf finale Antwort. |
| `SipClientTransactionRequest` | `Transactions/SipClientTransactionRequest.cs` | Eingabemodell: Methode, URI, Header, Body, Ziel, Transport, Timeout/T1/T2, Callbacks für Provisionals und retransmittierte INVITE-Fehler-Finals, Retention-Fenster (Timer D/K). |
| `SipClientTransactionResult` | `Transactions/SipClientTransactionResult.cs` | Ergebnis: finale Antwort, Provisionals, Sendeversuche. |
| `SipClientTransactionExecutor` (sealed, 742 Z.) | `Transactions/SipClientTransactionExecutor.cs` | UAC-Transaktion: Korrelation über Call-ID/CSeq/Via-Branch (genau 1 Via verlangt), Status-Normalisierung, UDP-Retransmission (INVITE: Verdopplung, Stopp nach Provisional = Timer A; Non-INVITE: E/T2-Kappung), Timeout = 64×T1 wenn Default, automatisches ACK für INVITE-3xx–6xx mit gleichem Branch (§17.1.1.3) inkl. Re-ACK bei Final-Retransmits, Completed-State-Absorption durch verzögertes Subscription-Dispose (Timer D=32 s / K=5 s), strenge Request-Validierung (Pflichtheader, Magic-Cookie-Branch, CSeq-Konsistenz). |
| `ISipServerTransactionEngine` | `Transactions/Server/ISipServerTransactionEngine.cs` | Registrierung eingehender Requests (Retransmission-Erkennung), Response-Versand durch Zustandsmaschine, Transport-Error-Handler (§17.2.4). |
| `SipServerTransactionEngine` (sealed, 549 Z.) | `Transactions/Server/SipServerTransactionEngine.cs` | UAS-Transaktionen: Timer T1/T2/T4, H/J/L (je 32 s); Via-Reflexion + §18.2.2-Response-Routing zentral **vor** Zieldermittlung (CF-040: erst reflektieren, dann rport lesen); Retransmit-Erkennung → letzte Antwort erneut senden; INVITE-2xx- und Nicht-2xx-Retransmits über `ScheduledActionScheduler` mit Verdopplung bis T2, Abbruch durch ACK (Tag-abgeglichen, auch 2xx-ACK mit anderem Branch); Discard weiterer finaler Antworten; Cleanup-Timer (Timer J bzw. L, nach ACK T4/0). |
| `SipServerTransactionKey` (record struct) | `Transactions/Server/SipServerTransactionKey.cs` | RFC 3261 §17.2.3-Matching-Schlüssel (CallId, Branch, SentBy, CSeq, Methode; ACK→INVITE); **Legacy-Fallback** ohne Magic Cookie (RFC 2543-Stil) über topVia+RequestUri+Tags. |
| `SipServerTransactionRegistration` (record struct) | `.../SipServerTransactionRegistration.cs` | Ergebnis: IsRetransmission/IsAck/ShouldProcess. |
| `SipServerTransactionResponseSnapshot` | `.../SipServerTransactionResponseSnapshot.cs` | Immutabler Response-Schnappschuss für Retransmission. |
| `SipServerTransactionState` (sealed) | `.../SipServerTransactionState.cs` | Mutabler Laufzeitzustand: Remote/Transport (unter Lock aktualisierbar), letzte Antwort, Interlocked-Flags (HasSeenRequest, RetransmitStarted…), Timer-Handles mit Replace/Cancel-Semantik, ACK-/Cleanup-CTS. |

### 3.4 Routing/ (6 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipRouteResolver` | `Routing/ISipRouteResolver.cs` | RFC 3263-artige Zielauflösung. |
| `SipDnsRouteResolver` (sealed, 420 Z.) | `Routing/SipDnsRouteResolver.cs` | DnsClient-basiert: IP-Literal → direkt; expliziter Port → A/AAAA; sonst NAPTR (SIP+D2U/T/W, SIPS+D2T/W) → SRV → A/AAAA-Fallback (inkl. `Dns.GetHostAddressesAsync`-Notnagel); IPv4-first-Sortierung; Transportkompatibilitätsmatrix (TLS nie downgraden); SRV-Fallback-Namenslisten je Wunschtransport; injizierbarer Zufall für deterministische Tests. |
| `SipRouteCandidate` | `Routing/SipRouteCandidate.cs` | Endpoint+Transport+Quelle („naptr+srv", „srv", „ip-literal", …). |
| `SipRouteResolutionRequest` / `-Result` | `Routing/SipRouteResolution*.cs` | Eingabe (Host, optional Port, Wunschtransport) / geordnete Kandidatenliste mit `Primary`. |
| `SipSrvWeightedOrdering` (static) | `Routing/SipSrvWeightedOrdering.cs` | Generische RFC 2782-Ordnung: Priorität aufsteigend, innerhalb einer Priorität gewichtete Zufallsziehung (Weight-0-Sonderfall) — Lastverteilung über Proxy-Farmen (CF-041). |

### 3.5 Authentication/ (4 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipDigestAuthenticator` | `Authentication/ISipDigestAuthenticator.cs` | Erzeugt Authorization-Header aus Challenge; Body-Parameter für `auth-int`. |
| `SipDigestAuthentication` (sealed) | `Authentication/SipDigestAuthentication.cs` | RFC 7616-Digest: MD5, MD5-sess, SHA-256(-sess), SHA-512-256(-sess, via BouncyCastle); qop-Auswahl auth > auth-int > legacy; A2 mit Body-Hash bei auth-int; nc/cnonce/opaque; Quoted-Value-Escaping. |
| `SipDigestChallengeSelector` (static) | `Authentication/SipDigestChallengeSelector.cs` | Wählt aus WWW-/Proxy-Authenticate die **stärkste unterstützte** Challenge (RFC 7616 §4-Scoring, unsupported = −1, synchron zu TryResolveAlgorithm zu halten) und liefert den passenden Antwort-Headernamen; `stale=true`-Erkennung. |
| `SipNonceCounter` (sealed) | `Authentication/SipNonceCounter.cs` | Koppelt nc an die Nonce (CF-042): Reset auf 1 bei neuer Nonce, Inkrement bei Wiederverwendung — eine Instanz pro Auth-Retry-Schleife. |

### 3.6 Observability/ (7 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipTelemetrySink` (public!) | `Observability/ISipTelemetrySink.cs` | Events/Metriken/CDRs. |
| `NullSipTelemetrySink` | `Observability/NullSipTelemetrySink.cs` | No-op-Singleton. |
| `InMemorySipTelemetrySink` | `Observability/InMemorySipTelemetrySink.cs` | Ringpuffer (Default 4096/Stream) für Tests/Diagnose. |
| `SipEventRecord` / `SipMetricRecord` / `SipCdrRecord` (public) | `Observability/Sip*Record.cs` | Strukturierte Records mit CallId/CorrelationId/TraceId; CDR mit Start/Ende/Outcome/Duration. |
| `SipIceTelemetrySink` | `Observability/SipIceTelemetrySink.cs` | Adapter `IIceTelemetrySink` → SIP-Events. |

### 3.7 Adapters/ (14 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `AdvertisedMediaAddressResolver` (static) | `Adapters/AdvertisedMediaAddressResolver.cs` | Ermittelt die zu annoncierende Medien-IP: konkreter Nicht-Loopback-Bind gewinnt; sonst OS-Routing-Probe (connected UDP-Socket, kein DNS) Richtung Remote-Signalisierung, dann SIP-URI-Host; Loopback nur mit Warnung als letzter Ausweg. |
| `CallMediaParametersIceEnricher` (static) | `Adapters/CallMediaParametersIceEnricher.cs` | Klont `CallMediaParameters` mit lokalen ICE-Credentials/Kandidaten/Rolle (RFC 8445 §5.1.1), inkl. Video-5-Tupel (RFC 8839). |
| `CallMediaParametersSrtpEnricher` (static) | `Adapters/CallMediaParametersSrtpEnricher.cs` | Rekonstruiert SDES-Keys aus den tatsächlich gesendeten/empfangenen SDP-Strings (lokal=Encrypt, remote=Decrypt), nur bei Suite-Übereinstimmung („nie halb-verschlüsselt"); Video-m-line separat; bewahrt ICE-Felder (dokumentierte Enricher-Reihenfolge-Invariante). |
| `CallMediaParametersDtlsEnricher` (static) | `Adapters/CallMediaParametersDtlsEnricher.cs` | DTLS-SRTP-Metadaten (RFC 5763): nur wenn beide Seiten Fingerprints signalisiert haben und kein SDES aktiv ist; lokale Rolle aus a=setup (RFC 4145 §4). |
| `LearnedPublicContact` (record) | `Adapters/LearnedPublicContact.cs` | Immutables NAT-gelerntes (Host, Port)-Paar hinter volatiler Referenz (keine Torn Reads). |
| `NatPublicContactState` (static) | `Adapters/NatPublicContactState.cs` | Pure/idempotente Entscheidungslogik: registrar-reflektierte Adresse übernehmen, `Changed` nur bei echter Änderung → selbstterminierende korrigierende Re-Registrierung. |
| `SipCallChannelConversions` (static) | `Adapters/SipCallChannelConversions.cs` | DialogState→CallState-Mapping; DTMF-Code→Symbol (RFC 4733). |
| `SipCallChannelFrameTap<TFrame>` (sealed) | `Adapters/SipCallChannelFrameTap.cs` | Pro Frame-Art (Audio/Video): Send-Delegate + Listener-Fan-out mit getrennten Locks und Fehlerisolierung je Listener. |
| `SipCallChannelNotifier` (sealed) | `Adapters/SipCallChannelNotifier.cs` | Consumer-Callback-Dispatch mit Pufferung früher State-/RemoteHold-Events bis `Bind`; Flush außerhalb des Locks (Deadlock-Schutz). |
| `SipCallChannelSrtpPolicyGuard` (sealed) | `Adapters/SipCallChannelSrtpPolicyGuard.cs` | Validiert Inbound-Offer gegen SRTP-Policy, sendet 488 bei Verstoß, publiziert Telemetrie. |
| `SipCallChannelSrtpTelemetry` (sealed) | `Adapters/SipCallChannelSrtpTelemetry.cs` | Emittiert `sip.media.srtp.policy.applied` und `…decision`-Events. |
| `SipCoreCallChannel` (sealed, 979 Z.) | `Adapters/SipCoreCallChannel.cs` | Adapter Session→`ICallChannel`: reserviert Medienports (UDP-Sockets) vor SDP-Bau, baut Offer/Answer über `ISdpNegotiator`, publiziert `MediaParametersNegotiated` (einmalig + Rekey-Republish bei re-INVITE mit Signaturvergleich), Fail-Closed bei „keyless secure negotiation", hält Live-SRTP-Keys für Hold/Unhold-Re-Offer (kein Rekey), DTLS-Latch, SDP-Origin-Versionierung (RFC 4566 §5.2), DTMF via RTP-Delegate mit SIP-INFO-Fallback, Attended Transfer (Replaces-Refer-To), Terminierung bei SRTP-Policy-Verstoß. |
| `SipLineChannel` (sealed, 714 Z.) | `Adapters/SipLineChannel.cs` | `ILineChannel`: REGISTER-Schleife (Refresh-Ratio, Backoff mit Exponent-Kappung, Auth-Fail = permanent, Reconnect-Callbacks), Call-ID/CSeq-Persistenz über Refreshes (§10.2.4), NAT-Lernen aus received/rport mit korrigierenden Re-Registrierungen (gedeckelt), Unregister mit derselben Binding-Identität, Inbound-INVITE-Zuordnung via `TrunkInboundMatcher` (inkl. gecachter Registrar-IPs), Outbound-Dial-Bootstrap mit Outbound-Proxy-Routeset (`;lr` erzwungen). |
| `TrunkInboundMatcher` (static) | `Adapters/TrunkInboundMatcher.cs` | Pure Entscheidungslogik: exakter User-Match, DID-Whitelist (restriktiv), Peer-Trust (Registrar-IP), Domain-Match — modelliert nach PJSIP/FreeSWITCH-Trunk-Routing. |

### 3.8 Signaling/Contracts (15 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `ISipCallSession` | `Contracts/ISipCallSession.cs` | Voll ausgestattete Dialog-Session-API: Zustände, Events (State/Hold/DTMF/Transfer/Subscription/Notify), Answer/Reject/Redirect/Hangup/Hold/Unhold, DTMF/INFO/REFER/OPTIONS/SUBSCRIBE/NOTIFY; Default-Interface-Member für LocalTag/RemoteTag/LocalSdp/Diversion/RemoteSignalingEndPoint (Abwärtskompatibilität). |
| `ISipCallSessionContext` | `Contracts/ISipCallSessionContext.cs` | Schmaler Laufzeitkontext für die Session-Services; dokumentierte Atomic-Snapshot-APIs `AdvertisedPublicContact` (HARD-C1) und `ActiveInvite`/`SetActiveInvite`/`ClearActiveInvite` (HARD-C2); `RouteSet` vs. `DialogRouteSet` (roh vs. mit Preloaded-Fallback) gegen Doppel-Rewrite. |
| `ISipCallSignalingService` | `Contracts/ISipCallSignalingService.cs` | INVITE-Ingress/-Egress; Events `IncomingInvite`, `OutboundCallStarted`; `SubscribeAsync` (RFC 6665). |
| `ISipIdentityTrustPolicy` / `ISipUasUserIdentityPolicy` | `Contracts/…` | Trust-Grenze für P-Asserted-Identity (RFC 3325) bzw. 404-Entscheidung (§8.2.2.1). |
| `ISipRegistrationService` | `Contracts/ISipRegistrationService.cs` | Register/Unregister/UnregisterAll(`Contact: *`)/FetchBindings. |
| `SipCallSessionConfiguration` / `SipCallSessionDependencies` | `Contracts/…` | Immutable Konfiguration (URIs, Auth, Route-Set, Custom-Header) bzw. DI-Bündel (Transport, Digest, Logger, ServerTransactions, TrustPolicy, SdpProvider). |
| `SipInviteRequest` / `SipRegistrationRequest` / `SipRegistrationResult`(+`SipRegistrationBinding`) / `SipSubscribeRequest` | `Contracts/…` | DTOs mit ausführlich dokumentierten RFC-Feldern (PreloadedRouteSet, Privacy, Referred-By; ExistingCallId/StartCSeq, InstanceId, Wildcard/Fetch, PublicHost/Port; ObservedPublicHost/Port aus Via). |
| `SipSessionSdpProvider` | `Contracts/SipSessionSdpProvider.cs` | Vier Delegates: BuildOffer, TryNegotiateAnswer, TryParseMediaParameters, IsRemoteHold. |
| `SipSubscriptionHandle` | `Contracts/SipSubscriptionHandle.cs` | `IAsyncDisposable`-Handle mit `NotifyReceived`-Event und idempotentem Unsubscribe. |

### 3.9 Signaling/Dialogs (19 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `SipCallSession` (sealed, 977 Z.) | `Dialogs/SipCallSession.cs` | Die Dialog-Fassade: Factory `CreateOutbound/CreateInbound`; hält Zustand unter `_sync`, `_operationGate` (SemaphoreSlim) serialisiert Operationen; AnswerAsync mit Require-Validierung (420), 100rel-Handshake, Session-Timer-Validierung (400/422) und 200 OK; Hangup je Zustand (486-Reject / CANCEL / BYE, mit atomarem Active-INVITE-Snapshot); Redirect (3xx, Record-Route entfernt); TransitionTo mit absorbierendem Terminated und Timer-Stopp; delegiert Inbound/Response an Services; CSeq-Validierung, PRACK-Ack, Asserted Identity, Diversion. |
| `SipCallSessionContextAdapter` (sealed) | `Dialogs/SipCallSessionContextAdapter.cs` | Implementiert `ISipCallSessionContext` als Lock-gesicherte Property-Brücke auf die Session (alle Paar-Snapshots unter einem Lock). |
| `SipCallSessionEventDispatcher` (sealed) | `Dialogs/SipCallSessionEventDispatcher.cs` | Fault-isolierter Event-Dispatch (DTMF/Transfer/Subscription/Notify); Subscription-Default = akzeptieren, Transfer-Default = ablehnen. |
| `SipCallSessionHeaderService` (sealed, 316 Z.) | `Dialogs/SipCallSessionHeaderService.cs` | Header-Bau für Requests (Via/From/To/Contact/Route/Supported, P-Preferred-Identity, Privacy-Anonymisierung nach RFC 3323 §4.1, Referred-By, SIPS-Contact-Zwang) und Responses (To-Tag-Sicherung, rport-Reflexion, Record-Route-Echo mit NAT-Begründung, advertised Contact als atomarer Snapshot); Custom-Header mit Protected-Liste und Header-Injection-Guard. |
| `SipCallSessionInboundService` (sealed, 998 Z.) | `Dialogs/SipCallSessionInboundService.cs` | UAS-Pfad der Session: Dialog-Identitäts-Gate (481 bei Tag-Mismatch, To-Tag-Pflicht für strikt-in-dialog Methoden, CF-013), CSeq-Ordnung, Require/Content/Session-Timer-Validierung; Handler für INFO (DTMF-Relay, 415 sonst), REFER (202/603 + implizite NOTIFY-Kette nach RFC 3515/4488), NOTIFY, SUBSCRIBE (Leases + NOTIFY), OPTIONS, UPDATE (Offer/Answer + Session-Timer), PRACK (RAck-Validierung), BYE (200 + Terminated + Reason), re-INVITE (491 bei pending, Auto-Answer mit SDP-Verhandlung, Hold-Erkennung, Rekey-SDP-Ablage), CANCEL (200 + 487), 501-Fallback. |
| `SipCallSessionInitialization` | `Dialogs/SipCallSessionInitialization.cs` | Factory-Zustand für Inbound (Ringing, InitialInvite, Tags) vs. Outbound (Idle). |
| `SipCallSessionTransactionService` (sealed, 903 Z.) | `Dialogs/SipCallSessionTransactionService.cs` | UAC-Pfad: INVITE-Schleife mit Auth-Retry (Authorization je Versuch neu generiert, frisches nc — CF-047), Stale-Nonce-Kappung (2), 422/Min-SE-Retry (RFC 4028, gedeckelt), 100rel/PRACK-In-Order-Kette (await-verkettete Sends statt ContinueWith — CF-044), ACK-Bau nach §13.2.2.4 mit Dialog-Routing, Cancelled-INVITE-Konsum (CANCEL kreuzt 200 → sofortiges BYE), Retry-After-Parsing (503), 488-Sonder-Reason; generischer In-Dialog-Request mit Digest-Retry über die **effektive** Request-URI (Strict-Router, CF-014); CANCEL mit atomarem (CSeq,Branch)-Paar; Fork-Delegation. |
| `SipCallSessionTransactionUtilities` (static) | `Dialogs/SipCallSessionTransactionUtilities.cs` | Route-Set aus Record-Route (reversiert), Supported-Token-Append, Reason-Header-Bau, Termination-Reason-Auflösung. |
| `SipCallSessionUtilities` (static, 322 Z.) | `Dialogs/SipCallSessionUtilities.cs` | DTMF-Validierung, Default-Reason-Phrasen, 100rel nur bei `Require: 100rel` (bewusst, Fritz!Box-Fallbeschreibung), 100rel-Sende-/Retransmit-Handshake mit 504-Timeout, Session-Refresh/-Expiry-Helfer (Gate-bewusst), Inbound-CSeq-Validierung (CANCEL=gleicher CSeq, 500+Retry-After bei Out-of-Order-INVITE), Asserted-Identity-Anwendung, Diversion-Parsing. |
| `SipDialogIdentity` (static) | `Dialogs/SipDialogIdentity.cs` | RFC 3261 §12.2.2-Tag-Matching inkl. Methodenliste, die einen To-Tag erzwingt. |
| `SipDialogManager` (sealed, 270 Z.) | `Dialogs/SipDialogManager.cs` | Early-/Confirmed-Dialog-Verwaltung pro Remote-Tag (Forking): Route-Set aus Provisional wird beim 2xx beibehalten, Remote-Target-Refresh (INVITE/UPDATE), aktive/letzte Dialog-Auswahl. |
| `SipDialogPath` | `Dialogs/SipDialogPath.cs` | Snapshot RemoteTag/RemoteTarget/RouteSet. |
| `SipDialogState` (enum) | `Dialogs/SipDialogState.cs` | Idle/Inviting/Ringing/Established/OnHold/Terminated. |
| `SipFinalResponseException` | `Dialogs/SipFinalResponseException.cs` | Transaktionsfehler mit finaler Response im Gepäck. |
| `SipForkedInviteHandler` (sealed, 253 Z.) | `Dialogs/SipForkedInviteHandler.cs` | Nach Abschluss der INVITE-Transaktion: zusätzliche 2xx anderer Fork-Zweige ACKen und nicht gewählte Dialoge per BYE beenden (einmal je Tag; Warning-Level bei Fehlern). |
| `SipInDialogRequestRouting` (static) + `SipInDialogRoutingPlan` (record struct) | `Dialogs/SipInDialogRequestRouting.cs` | §12.2.1.1-Komposition (loose/strict/leer) getrennt von Transportziel: geroutete Dialoge → aufgelöster Topmost-Route-Hop, direkte Dialoge → gelernte Response-Quelle (NAT-freundlich, „other information"); Preloaded-Set nicht doppelt planen; PRACK kann Direktziel pinnen. |
| `SipInboundSessionContext` | `Dialogs/SipInboundSessionContext.cs` | InitialInvite + LocalTag für die Inbound-Factory. |
| `SipResponseEnvelope` (record struct) | `Dialogs/SipResponseEnvelope.cs` | (RemoteEndPoint, SipResponse)-Paar. |
| `ReliableProvisionalSendContext` | `Dialogs/ReliableProvisionalSendContext.cs` | Parameterobjekt für den 100rel-Sendefluss (HARD-R7). |

### 3.10 Signaling/Events (7), /Formatting (7), /Identity (3)

- **Events:** `SipDialogStateChangedEventArgs` (Old/New/TerminationReason), `SipDialogTerminationReason` (RFC 3326-Modell inkl. `RetryAfterSeconds` aus 503), `SipDtmfReceivedEventArgs`, `SipIncomingInviteEventArgs`, `SipNotifyReceivedEventArgs`, `SipSubscriptionRequestedEventArgs` (mit `Accept`-Rückkanal), `SipTransferRequestedEventArgs` (mit `Accept`).
- **Formatting:** `AttendedTransferReferTo` (RFC 5589-Refer-To mit URI-escaptem Replaces), `SipContentPolicy` (415-Regeln, RFC 5621 handling=optional), `SipReasonHeader` (Parse/Format RFC 3326 inkl. Quoted-Pair), `SipReplacesHeaderValue` (RFC 3891 Parse/Format/Dialog-Matching in beiden Tag-Orientierungen), `SipRequireOptionPolicy` (unterstützte Option-Tags: 100rel, timer, replaces, norefersub → 420/Unsupported), `SipSessionTimerPolicy` (RFC 4028: Default 1800 s, Min 90 s, 422-Handling, Refresher-Auflösung), `SipSignalingFormat` (Via-/Contact-Bau mit advertised Host/Port, `;rport`, transport-Param, sips-Schema).
- **Identity:** `AcceptAllSipUasUserIdentityPolicy` (Default: alles bedienen), `DenyAllSipIdentityTrustPolicy` (Default: niemandem P-Asserted-Identity glauben — sicherer Default), `SipAssertedIdentityHeader` (Parse/Format RFC 3325).

### 3.11 Signaling/Ingress (8), /Registration (2), /Reliability (3), /SessionTimers (1), /Subscriptions (4)

| Typ | Datei | Beschreibung |
|---|---|---|
| `SipCallSignalingService` (sealed, 974 Z.) | `Ingress/SipCallSignalingService.cs` | Zentraler Dispatcher: abonniert Transport; Outbound-INVITE mit Zielwarteschlange (Redirect-3xx-Fanout, 413/415-Body-Reduktion, 416-SIPS-Downgrade, 420-Require-Filter, synthetische 408/503), `OutboundCallStarted` erst nach Erfolg (HARD-C3); Inbound-Pipeline: Ingress-Validierung (400/416), Server-Transaktions-Registrierung, Loop-Detection (eigene Branches, 482), Max-Forwards (483), Require (420), Content (415), Merged-INVITE (482), 100 Trying, Session-Dispatch per Call-ID, CANCEL/OPTIONS/NOTIFY-Sonderwege, 481 für dialoggebundene Methoden ohne Session, 501-Fallback, Replaces-Validierung (481/400) mit Terminierung des ersetzten Dialogs nach Established, 404 via UserIdentityPolicy; Lifecycle-Hook publiziert State-Events + CDR und disposed Sessions bei Terminated. |
| `SipCallSignalingSubscriptions` (sealed, 368 Z.) | `Ingress/SipCallSignalingSubscriptions.cs` | Out-of-dialog SUBSCRIBE (RFC 6665 §4.1): Kandidaten-Iteration, Digest-Retry (Selector, CF-043), Refresh-Loop bei 90 % der Lease, Unsubscribe (Expires 0) über Handle, NOTIFY-Dispatch inkl. terminated-Aufräumen. |
| `SipIngressRequestPolicy` (static) | `Ingress/SipIngressRequestPolicy.cs` | Transport-Erkennung aus Via, Pflichtheader-/CSeq-Methoden-Prüfung, URI-Schema (416), Loop-Erkennung über lokales Branch-Präfix, Max-Forwards-Prüfung/-Dekrement. |
| `SipInitialRequestRoutingPlanner` (static) | `Ingress/SipInitialRequestRoutingPlanner.cs` | Preloaded-Route-Set-Planung (loose vs. strict router, §8.1.2/§12.2.1.1-Analogie); validiert jede Route-URI. |
| `SipMergedInviteTracker` (sealed) | `Ingress/SipMergedInviteTracker.cs` | RFC 3261 §8.2.2.2: Merged-Request-Erkennung (gleiches Call-ID/From-Tag/CSeq, **anderer** Branch) vs. Retransmission; TTL 2 min, amortisierte Bereinigung. |
| `SipOutboundInviteRetryPolicy` (static) | `Ingress/SipOutboundInviteRetryPolicy.cs` | Redirect-Fanout, SIPS→SIP-Downgrade, 420-Unsupported-Filter, synthetische Final-Response-Exceptions; `IsTransportFailure` = InnerException-Heuristik. |
| `SipOutboundInviteTarget` (record) / `SipOutboundSubscriptionEntry` | `Ingress/…` | Ziel-Tupel (RequestUri/LogicalRemoteUri/RouteSet/NextHop) bzw. Subscription-Zustand (Tags, CSeq, Expires, RefreshCts, Handle). |
| `SipRegistrationFailedException` | `Registration/SipRegistrationFailedException.cs` | REGISTER-Fehler mit StatusCode/ReasonPhrase (SipLineChannel prüft 401/403 = permanent). |
| `SipRegistrationService` (sealed, 778 Z.) | `Registration/SipRegistrationService.cs` | REGISTER-Familie (Register/Unregister/UnregisterAll/Fetch): Redirect-Queue, 423/Min-Expires-Retry, Digest- und Stale-Retry (gedeckelt), klare Fehlermeldung bei Challenge ohne Passwort, Effective-Expires-Auswahl (eigenes Binding > Expires-Header > längstes Binding > Fallback; ausführlich begründet), Service-Route, Bindings-Parsing, Via-received/rport-Extraktion für NAT, Path/outbound-Supported-Tokens, +sip.instance/ob/reg-id-Contact. |
| `SipReliableProvisionalEntry` / `-Manager` / `-ReceiptOrder` | `Reliability/…` | UAS-Seite: Entry mit TCS pro RSeq; Manager mit zufällig startender RSeq-Sequenz, RAck-Validierung (400/481), Timeout/Dispose-Aufräumen. UAC-Seite: strikte In-Order-Annahme (Gaps/Duplikate nicht PRACKen — CF-044). |
| `SipSessionTimerManager` (sealed, 239 Z.) | `SessionTimers/SipSessionTimerManager.cs` | RFC 4028-Laufzeit: als Refresher UPDATE bei Intervall−Sicherheitsmarge (⅓, 5–600 s clamp), sonst Expiry bei Intervall+Grace (10 %, 2–30 s); Expiry → BYE + Terminated(408); einmalige Auslösung pro Aktivierung. |
| `SipSubscriptionIdentifier` / `-Lease` / `-LifecycleManager` / `-LifecycleUpdate` | `Subscriptions/…` | UAS-Subscriptions: Identität (Event-Package + id, normalisierter Key), Lease mit CTS und Ablaufzeit, Manager mit Activate/Refresh/Terminate + Timeout-NOTIFY („terminated;reason=timeout"), Update-DTO (Expires + Subscription-State-Header). |

---

## 4. Zentrale Abläufe & Datenflüsse

### 4.1 Eingehende Nachricht → Session
`SipTransportRuntime` (UDP-Loop bzw. Stream-/WS-Receive über `HandleInboundPayloadAsync`) → `SipWireProtocol.TryParseRequest/-Response` → Dispatch an Handler-Snapshot. Für Requests läuft `SipCallSignalingService.HandleInboundRequest` synchron auf dem Empfangsthread: Ingress-Validierung → `SipServerTransactionEngine.RegisterInboundRequest` (Retransmission ⇒ letzte Antwort erneut, ACK ⇒ Timer-Stopp) → Loop-/Max-Forwards-/Require-/Content-/Merged-Checks → bei INVITE sofort 100 Trying → existierende Session per Call-ID ⇒ `SipCallSessionInboundService` (fire-and-forget-Task); sonst methodenspezifische stateless Antworten (OPTIONS 200, CANCEL 481, dialoggebundene 481, 501) oder Erzeugung einer Inbound-Session (Ringing) + `IncomingInvite`-Event → `SipLineChannel.HandleIncomingInvite` (Line-Matching, NAT-Contact) → neuer `SipCoreCallChannel` → App-Callback. Antworten laufen immer durch `SipServerTransactionEngine.SendResponseAsync`, das zentral Via reflektiert und das UDP-Ziel aus received/rport ableitet (CF-040-Reihenfolge).

### 4.2 Outbound-INVITE-Lebenszyklus
`SipLineChannel.StartOutboundDialAsync` (SDP-Offer mit reserviertem RTP-Port) → `SipCallSignalingService.InviteAsync`: Zielplanung (Preloaded-Route-Set loose/strict) → DNS-Kandidaten → pro Kandidat `SipCallSession.CreateOutbound` + `StartOutboundInviteAsync` (Gate wird vor der Transaktion freigegeben, damit CANCEL möglich bleibt) → `SipCallSessionTransactionService.SendInviteTransactionAsync`: Branch/CSeq setzen (`SetActiveInvite`), Session-Timer-Offer-Header, `SipClientTransactionExecutor.ExecuteAsync` mit Provisional-Callback (Dialog-Update, Ringing-Übergang, 100rel-in-order-PRACK-Kette) → 2xx: Remote-Tag, `ClearActiveInvite`, ACK über Dialog-Routing, Remote-SDP, Session-Timer, Established; 401/407: Challenge merken, CSeq++, Schleife (Authorization mit frischem nc regeneriert); 422: Min-SE übernehmen, Retry; 3xx–6xx: ACK durch die Transaktion selbst, Termination-Reason, `SipFinalResponseException` → Ingress-Retry-Policy (Redirect/413/415/416/420) oder Fehlschlag. Kreuzt ein CANCEL die 200: `TryConsumeCancelledInvite` ⇒ sofortiges BYE. Verspätete 2xx anderer Fork-Zweige behandelt `SipForkedInviteHandler` (ACK + BYE).

### 4.3 Inbound-Answer inkl. 100rel
`SipCoreCallChannel.AnswerAsync` → SRTP-Policy-Gate (488 bei Verstoß) → SDP-Answer bauen → `SipCallSession.AnswerAsync`: Require-Validierung (420) → falls INVITE `Require: 100rel` trägt: 180 mit RSeq/Require:100rel, UDP-Retransmit mit Backoff, PRACK-Wait (Timeout ⇒ 504 + Terminated) → Session-Timer-Validierung (400/422) → 200 OK mit SDP → Established → Channel publiziert `MediaParametersNegotiated` (Enricher-Kette ICE→SRTP→DTLS, Fail-Closed bei schlüsselloser „sicherer" Verhandlung) und gibt die Portreservierungs-Sockets frei.

### 4.4 Registrierung
`SipLineChannel.RegisterAsync`-Schleife: Request mit persistiertem Call-ID/CSeq → `SipRegistrationService.ExecuteRegisterAsync` (Kandidaten, Digest-/Stale-/423-/Redirect-Retries) → Ergebnis mit `ObservedPublicHost/Port` aus dem Via → `NatPublicContactState.ApplyObserved` ⇒ ggf. sofortige korrigierende Re-Registrierung (gedeckelt) → Registered, Refresh-Delay (`ComputeRefreshDelay`: ratio×Lifetime, geclampt) → erneut. Fehler: Auth ⇒ Failed (permanent), sonst Backoff/Reconnecting bis MaxRetries. Stop/Dispose: Loop canceln, Unregister mit derselben Binding-Identität (Expires 0).

### 4.5 Timer/Retransmissions (Zusammenfassung)
- **UAC:** Timer A (INVITE-Verdopplung bis Provisional), E (Non-INVITE, Kappung T2), B/F ≈ 64×T1-Gesamttimeout, D (32 s Fehler-Absorption)/K (5 s) als verzögertes Subscription-Dispose; ACK-Wiederholung für retransmittierte Fehler-Finals.
- **UAS:** Timer G (Verdopplung bis T2) für INVITE-Finals (2xx und Nicht-2xx, jeweils bis TimerH=32 s bzw. ACK), J/L (32 s) Cleanup, nach ACK T4 (5 s, UDP) bzw. sofort.
- **100rel:** UAS-Retransmit des 180 mit T1→T2-Backoff bis PRACK/Timeout; UAC strikte RSeq-Ordnung.
- **RFC 4028:** Refresh bei Intervall−max(⅓, clamp), Expiry bei Intervall+Grace ⇒ BYE(408).

### 4.6 Auth-Challenge-Handling
Einheitliches Muster in REGISTER/INVITE/In-Dialog/SUBSCRIBE: 401/407 → `SipDigestChallengeSelector.TrySelect` (stärkster Algorithmus, korrekter Header) → `SipDigestAuthentication` (nc über `SipNonceCounter` an Nonce gekoppelt) → CSeq++ → Retry; stale=true-Retries auf 2 gedeckelt. Besonderheit CF-014: Digest-URI ist die effektive (ggf. strict-rewritten) Request-URI; Besonderheit CF-047: im INVITE-Loop wird die Authorization je Sendung neu erzeugt statt wiederverwendet.

---

## 5. Threading-/Speicher-/Fehlerbehandlungsmodell

- **Nebenläufigkeit:** Empfangs-/Accept-Loops als langlaufende `Task.Run`-Tasks mit gemeinsamem `CancellationTokenSource _stop`. Handler-Registrierung über `ConcurrentDictionary` + `Interlocked`-IDs; Dispatch über Array-Snapshots (Selbstabmeldung während Iteration sicher). Sessions: grobkörniges Monitor-Lock `_sync` für Zustand + `SemaphoreSlim _operationGate` für Operationsserialisierung; Gate wird vor langlaufenden INVITE-Transaktionen bewusst freigegeben (CANCEL-Deadlock-Vermeidung, dokumentiert). Paarige Zustände (Host/Port, CSeq/Branch) haben explizite Atomic-Snapshot-APIs (HARD-C1/C2). Einfache Flags via `Interlocked.Exchange`/`Volatile.Read` (`_disposed`-Idiom durchgängig). Cross-Thread-veröffentlichte Referenzen (`LearnedPublicContact`, `_localAnswerSdp`, …) sind `volatile` und immutabel.
- **Async-Muster:** durchgängig `ConfigureAwait(false)`; `TaskCompletionSource` mit `RunContinuationsAsynchronously`; `Task.WhenAny` + `WaitAsync(token)` für Timeout-Konvertierung; Fire-and-Forget stets mit try/catch+Log (`FireAndForgetAck`, Ingress-Antworten, Fork-Handling); PRACK-Sends als await-verkettete Task-Kette statt `ContinueWith` (Fehlerpropagation, CF-044).
- **Timer:** Server-Transaktionen über injizierten `IScheduledActionScheduler` (Handles mit Replace/Dispose-Disziplin in `SipServerTransactionState`); Session-Timer/Subscriptions/100rel über `Task.Delay` + verknüpfte CTS mit Lifecycle-CTS.
- **Puffer/Speicher:** Wire-Parser cappt bei 64 KiB; Stream-Framer 64 KiB Header/256 KiB Body mit Exception→Verbindungsabbau; WS-Aggregation an Framer-Limits gekoppelt; Telemetrie-Ringpuffer begrenzt; Merged-INVITE-Tracker mit TTL+amortisierter Bereinigung; Transaktions-Cleanup-Timer verhindern Dictionary-Leaks. Framer nutzt `List<byte>`+`CollectionsMarshal.AsSpan` (O(n)-Kopien, für Signalisierungsraten ok); `GC.AllocateUninitializedArray` für Frames.
- **Dispose:** Idempotent via `Interlocked.Exchange`; Transport-Dispose stoppt Listener, cancelt Loops, `Task.WaitAll` mit 2-s-Deckel, entsorgt Pools/Verbindungen; Stream-/WS-Verbindungen joinen ihren Receive-Loop synchron (blockierender Dispose); Session-Dispose entsorgt 100rel/SessionTimer/Subscriptions/Gate; SignalingService entsorgt Sessions und cancelt Subscription-Refreshes. `ReleaseOperationGateSafe` toleriert die Dispose-Race (`ObjectDisposedException` geschluckt).
- **Fehlerbehandlung:** Netz-/Parsingfehler werden fast ausnahmslos auf Debug/Warning geloggt und lokal isoliert (defensive „log-and-continue"-Philosophie in Loops); harte Fehler an API-Grenzen als aussagekräftige Exceptions (`SipFinalResponseException`, `SipRegistrationFailedException`, TimeoutException mit Kontext); Konsumenten-Callbacks überall fault-isoliert.

---

## 6. Qualitätsbefunde

### Stärken
- Außergewöhnlich hohe, **dokumentierte RFC-Dichte** mit Paragraphenangaben und nachvollziehbaren Fix-Markern (CF-xxx/HARD-xxx); viele subtile Compliance-Punkte korrekt gelöst (rport-Reflexionsreihenfolge, strict-router Digest-URI, nc/Nonce-Kopplung, In-Order-PRACK, Merged- vs. Retransmitted-INVITE, Fork-Handling, Effective-Expires-Auswahl, gewichtete SRV-Ordnung).
- Saubere Schichtung, kleine Pure-Function-Policies, konsequente DI-Schnittstellen, explizite Atomic-Snapshot-APIs gegen Torn Reads.
- Sicherheitsbewusstsein: DoS-Limits auf jeder Ebene, Header-Injection-Guards, SDP-Secret-Redaction im Trace-Log, Deny-All-Identity-Default, gedeckelte Auth-/422-/Redirect-Retries, RFC 5922-SAN-Prüfung, TLS 1.2/1.3-only.

### Potenzielle Bugs / Risiken (mit Fundstellen)

1. **Fehlendes Re-ACK auf retransmittierte 2xx (UAC, RFC 3261 §13.2.2.4).** Nach dem 2xx wird `ClearActiveInvite()` gerufen (`/home/user/callora-voip-sdk/src/Core/Infrastructure/Sip/Signaling/Dialogs/SipCallSessionTransactionService.cs:200`) und die Response-Subscription des Executors für erfolgreiche INVITEs sofort entsorgt (`ResolveCompletedStateRetention` liefert `TimeSpan.Zero` für 2xx, `.../Transactions/SipClientTransactionExecutor.cs:371-375`). Ein danach retransmittiertes 2xx desselben Dialogs erreicht nur noch `SipForkedInviteHandler.TryGetInviteSuccessForkCandidate`, das `inviteCseq != _context.ActiveInviteCSeq` (jetzt 0) prüft (`.../Dialogs/SipForkedInviteHandler.cs:181`) und ablehnt. Geht das erste ACK verloren, wird das wiederholte 2xx nie erneut geACKt → UAS retransmittiert bis Timeout und beendet ggf. den Call.
2. **Malformiertes `+sip.instance`-Contact-Parameter (RFC 5626 §4.1).** `BuildContactHeaderValue` emittiert `;"+sip.instance"="<urn>"` — der Parameter-**Name** ist fälschlich gequotet (`/home/user/callora-voip-sdk/src/Core/Infrastructure/Sip/Signaling/Registration/SipRegistrationService.cs:567`). Korrekt wäre `;+sip.instance="<urn:...>"`. Strenge Registrare könnten den Contact ablehnen bzw. Outbound-Korrelation scheitert.
3. **Kein Digest-Retry beim RFC 4028-Refresh-UPDATE.** `SendSessionRefreshUpdateAsync` (`.../Dialogs/SipCallSessionTransactionService.cs:479-519`) nutzt nicht den auth-fähigen `SendInDialogRequestAsync`-Pfad; ein 401/407 auf das Refresh-UPDATE ⇒ `false` ⇒ `SipSessionTimerManager` wertet „refresh failed" und terminiert den Dialog per BYE (`.../SessionTimers/SipSessionTimerManager.cs:128-137`). Hinter auth-fordernden Proxies werden gesunde Calls nach dem ersten Refresh-Intervall abgebaut.
4. **Subscription-Refresh: kein Auth-Retry + Expires-Clamp.** `SendSubscribeRefreshAsync` behandelt 401/407 nicht (`.../Ingress/SipCallSignalingSubscriptions.cs:259-305`), und `negotiatedExpires = Math.Max(60, parsedExpires)` (`:179`, analog `:304`) hebt eine vom Server gewährte Lease < 60 s künstlich an — der Refresh bei 90 % von 60 s käme dann nach Ablauf der echten Lease. Zudem: `SubscribeAsync` mit `ExpiresSeconds=0` ergäbe `entry.ExpiresSeconds=0` (wenn der Server keinen Expires-Header liefert) und damit eine Busy-Loop im Refresh (`RunSubscriptionRefreshAsync`, `:311`, Delay 0).
5. **`IsTransportFailure`-Heuristik zu grob.** `exception.InnerException is not null` (`.../Ingress/SipOutboundInviteRetryPolicy.cs:36-37`) klassifiziert jede InvalidOperationException mit Inner als Transportfehler ⇒ Kandidaten-Failover + synthetisches 503 auch bei Nicht-Transport-Fehlern (z. B. PRACK-Fehlschlag, der als `InvalidOperationException` aus der PRACK-Kette propagiert).
6. **TOCTOU bei WS-Listener-Portallokation.** `AllocateEphemeralPort` bindet kurz einen TcpListener auf Loopback, gibt den Port frei und `HttpListener` bindet ihn danach auf `+:` (`.../Transport/SipTransportRuntimeUtilities.cs:12-19`, `.../SipTransportRuntime.cs:347-353`) — Race mit anderen Prozessen; außerdem prüft die Probe nur Loopback, nicht Any. Fehler wird zwar abgefangen (Listener bleibt aus), aber ein WS-Transport kann so sporadisch fehlen.
7. **Re-INVITE-Fallback-Antwort kann Offer/Answer verletzen.** Beim Auto-Answer auf re-INVITE/UPDATE fällt der Code bei fehlgeschlagener Verhandlung auf `BuildOffer` als 200-OK-Body zurück (`.../Dialogs/SipCallSessionInboundService.cs:295-297` und `:741-744`) — ein „Answer", das nicht auf das Offer antwortet (RFC 3264-Verstoß); sauberer wäre 488.
8. **UAC-Response-Matching akzeptiert fehlenden Branch.** `MatchesTransaction` lässt Responses ohne Via-Branch passieren (`.../Transactions/SipClientTransactionExecutor.cs:581-588`) — bei mehreren parallelen Transaktionen gleicher CSeq-Methode und Call-ID (theoretisch) fehlerhafte Zuordnung möglich; praktisch geringes Risiko, aber lockerer als §17.1.3.
9. **`ResolveTransactionTimeout`-Wertesemantik.** Ein Aufrufer, der explizit 32 s (== Default) mit abweichendem T1 setzt, bekommt stillschweigend 64×T1 statt 32 s (`.../SipClientTransactionExecutor.cs:384-396`).
10. **Blockierendes DNS auf dem Inbound-Pfad.** `SipLineChannel.IsSessionForThisLine` → `ResolveTrustedRegistrarAddresses` macht synchrones `Dns.GetHostAddresses` (`.../Adapters/SipLineChannel.cs:581-607`), falls der Cache noch leer ist (INVITE vor erster erfolgreicher Registrierung) — blockiert den Transport-Dispatch-Thread.
11. **Dispose kann blockieren.** `SipStreamConnection.Dispose`/`SipWebSocketConnection.Dispose` joinen den Receive-Loop synchron (`.../Transport/SipStreamConnection.cs:160-170`, `SipWebSocketConnection.cs:163-172`); hängt der Loop in einem langsamen `_onFrameAsync`-Dispatch, blockiert der Dispose entsprechend.
12. **Kleinere Code-Smells:** In `SipCallSession.RedirectAsync` ist die Pattern-Variable `stripped` tot und das Lambda unnötig verschachtelt (`.../Dialogs/SipCallSession.cs:510-513`). Inbound-Sessions bekommen hartkodiert `UserAgent = "CalloraVoipSdk/1.0"` und `Timeout = 30 s` statt der Line-Konfiguration (`.../Ingress/SipCallSignalingService.cs:600-601`). `DecrementMaxForwardsIfPresent` auf UAS-Ebene ist Proxy-Semantik ohne Wirkung (harmlos, `.../Ingress/SipIngressRequestPolicy.cs:141-154`). `NormalizeTransport` ist eine Identitätsfunktion (toter Pfad, `.../Routing/SipDnsRouteResolver.cs:400`). `SipReliableProvisionalManager` nutzt `new Random()` statt `Random.Shared` (`.../Reliability/SipReliableProvisionalManager.cs:14`).
13. **Design-Lücken (bewusst, aber erwähnenswert):** Der UAS challengt eingehende Requests nie (keine 401-Erzeugung; Vertrauen an Trunk-/Peer-Matching delegiert). REFER-Progress-NOTIFYs werden optimistisch „100 Trying → 200 OK" gemeldet, ohne den referierten Call zu verfolgen (im Code als Follow-up dokumentiert, `.../Dialogs/SipCallSessionInboundService.cs:822-831`). Redirect-Fanout (3xx) ist nur durch die Visited-Menge begrenzt — ein bösartiger Redirect mit vielen Contacts erzeugt viele sequenzielle Transaktionen.
14. **TODO-Marker:** Es existieren keine `TODO`/`FIXME`-Kommentare im Bereich; offene Punkte sind stattdessen als „follow-up"-Prosa dokumentiert (z. B. REFER-Progress, s. o.).

### Gesamteinschätzung
Ein für ein SDK ungewöhnlich vollständiger, sorgfältig dokumentierter SIP-Stack mit klarer Schichtung und starker Testbarkeit. Die gefundenen Risiken sind überwiegend Randfälle; am gewichtigsten sind (1) das fehlende Re-ACK auf 2xx-Retransmits, (2) der malformierte `+sip.instance`-Parameter und (3/4) die fehlenden Auth-Retries auf Refresh-Pfaden (Session-Timer-UPDATE, SUBSCRIBE-Refresh), da sie in realen Deployments (Paketverlust, strenge Registrare, auth-fordernde Proxies) zu Callabbrüchen bzw. Interop-Problemen führen können.



---

# Teil 2 — Medien-Transport: RTP/RTCP/SRTP/DTLS (124 Dateien)


**Umfang:** `src/Core/Infrastructure/Rtp/` (85 Dateien), `src/Core/Infrastructure/Rtcp/` (3), `src/Core/Infrastructure/Srtp/` (15), `src/Core/Infrastructure/Dtls/` (16), `src/Core/Application/Media/Rtcp/` (21). Alle Pfadangaben unten sind relativ zu `/home/user/callora-voip-sdk/src/Core/`.

---

## 1. Überblick & Verantwortung

Der Stack implementiert den vollständigen Medien-Transport eines VoIP-/WebRTC-SDKs — **transport-only**: Das SDK codiert nie selbst (Audio-/Video-Encoding liefert die Anwendung), es paketiert, verschlüsselt, transportiert, entschlüsselt und reassembliert.

Zwei parallele Session-Familien:

1. **SIP-/Einzelstream-Pfad** (`RtpSession` → `RtpCallMediaSession`, optional `VideoRtpStream`): ein Socket pro m-line, SDES- (RFC 4568) oder DTLS-SRTP-Keying, adaptiver Jitter-Buffer mit Playout-Loop, Symmetric RTP/Comedia-Latching, RFC-4733-DTMF, PLC (Concealment).
2. **BUNDLE-/WebRTC-Pfad** (`Bundled*`-Klassen, RFC 8843): ein gemeinsamer 5-Tupel-Socket für alle m-lines, MID/RID-Demux (RFC 9143/8852), ausschließlich DTLS-SRTP, eigener RTCP-Reporter mit RFC-3550-§6.2-Intervallberechnung, TURN-Relay-Datenpfad (RFC 8656), Simulcast-Senden (RFC 8853).

**Abgedeckte RFCs (explizit referenziert und implementiert):**

| RFC | Umsetzung |
|---|---|
| 3550/3551 | RTP-/RTCP-Wire-Format, §A.1-Sequenzvalidierung, §6.4.1-SR/RR/Reportblöcke, §A.8-Jitter, §6.2/6.3-Sendeintervall, §8.2-SSRC-Kollision, §6.5-SDES/CNAME, §6.6-BYE |
| 3711 (SRTP) | AES-CM (§4.1), HMAC-SHA1 (§4.2), KDF (§4.3), Replay-Window (§3.3.2), ROC (§3.3.1), SRTCP (§3.4), Key-Zeroing (§9.4) |
| 4568 (SDES) | Suite-Parsing, `inline:`-Keys, per-m-line-Keying |
| 4585/5104 | Generic NACK, PLI, FIR (Decode+Encode; FIR wird empfangen, nicht generiert) |
| 4588 (RTX) | OSN-Encapsulation, separater SRTP-Kontext (§9), Retransmit-Buffer, Reorder-Window |
| 5764/5763 (DTLS-SRTP) | `use_srtp`-Extension, `EXTRACTOR-dtls_srtp`-Export, Fingerprint-Auth (RFC 8122), §5.1.2-Demux |
| 8285 | One-Byte-Header-Extensions (0xBEDE): transport-cc, MID, RID |
| 5761/7983 | RTP/RTCP/STUN/DTLS-Demux auf einem Socket |
| TWCC (draft-holmer-rmcat-…-01) | Header-Extension-Stempel, Feedback-Builder/-Codec/-Interpreter, GCC-artige Delay-Trend-+Loss-Schätzung mit AIMD-Bitratenempfehlung |
| 3611 (XR) | VoIP-Metrics-Block (Decode-only) |
| 4733 | Telephone-Event (DTMF) senden/empfangen (beide Pfade) |
| 7022 | Opake, zufällige CNAME |
| 7675 | ICE-Consent-Verlust → Sendestopp (via ICE-Attachments, außerhalb des Bereichs) |

---

## 2. Architektur & Schichtung

### 2.1 Schichten

```
Application/Media/Rtcp/Packets  → reine RTCP-Paketmodelle (POCOs, wire-frei)
Application/Media/Rtcp/Wire     → IRtcpPacketCodec (Port)
Infrastructure/Rtcp/Wire        → RtcpPacketCodec (Compound), RtcpFeedbackCodec (NACK/PLI/FIR), RtcpTransportFeedbackCodec (TWCC)
Infrastructure/Rtp/Packets      → RtpPacket-Modell + RFC-8285-Extension-Codecs
Infrastructure/Rtp/Wire         → IRtpPacketCodec / RtpPacketCodec
Infrastructure/Srtp             → Krypto-Kontexte (per Richtung), KDF, AES-CM, Replay
Infrastructure/Dtls             → BouncyCastle-basierter Handshake + Key-Export
Infrastructure/Rtp/Session      → RtpSession (Socket, Demux, Sende-/Empfangspfad)
Infrastructure/Rtp (Root)       → Orchestrierung: RtpCallMediaSession / VideoRtpStream (SIP)
                                  und Bundled* (WebRTC-BUNDLE)
```

Die Trennung **Modell ↔ Wire ↔ Session** ist konsequent: RTCP-Modelle liegen in der Application-Schicht, die Binärcodecs in Infrastructure; RTP-Modell und -Codec sind Infrastructure-intern. Die Codecs sind zustandslos, Sessions besitzen die Zustände.

### 2.2 Jitter-Buffer-Design (`JitterBuffer/`)

Pull-Modell: `Add(packet, arrival)` + `TryGetNext(now)`, getrieben von einem `PeriodicTimer`-Playout-Loop in `RtpCallMediaSession` (Intervall = Paketdauer/4, geklemmt 2–10 ms). Kernideen:
- **Extended-Seq** über signiertes 16-Bit-Delta (wrap-sicher), `SortedDictionary<long, RtpPacket>`.
- **Referenzpunkt-Scheduling:** erstes Paket ankert (RTP-TS, Wanduhr+InitialDelay); Playout-Zeit = Anker + TS-Delta/ClockRate.
- **Adaptive Verzögerung:** +1…4 ms pro „Late“-Paket (aus Jitter abgeleitet), −0,5 ms pro pünktlichem Paket; Floor = MinDelay + Jitter·1,25 + RTT·0,10, geklemmt [20, 300] ms. Bei Erhöhung wird der Referenz-Playout verschoben, damit gepufferte Pakete nicht sofort „late“ werden.
- **RFC-3550-§6.4.1-Jitter** mit korrektem `unchecked`-uint-Wrap (der Kommentar dokumentiert einen früheren Sättigungs-Bug).
- **Stalled-Timestamp-Filter** (Comfort Noise / wiederholte TS) wird als Duplikat behandelt, um Fake-Jitter zu vermeiden.
- RTT-Seed 100 ms; erste echte RTCP-Probe ersetzt den Seed hart, danach EWMA (0,2).

Der BUNDLE-Pfad hat **keinen** Jitter-Buffer (dokumentiert); Video nutzt stattdessen den `VideoReorderBuffer` (Contiguous-Release, Gap-Hold bis `depth`, dann Skip).

### 2.3 Congestion-Control-Design (`CongestionControl/`)

Klassische zweiseitige TWCC-Pipeline, nur für Video und nur bei ausgehandeltem `a=extmap`:

- **Empfängerseite:** `TransportCcFeedbackSender` (paketgetriggert, ~100 ms) → `TransportCcArrivalRecorder` (lock-geschützter Ring, 1024) → `TransportCcFeedbackBuilder` (Unwrap der 16-Bit-Seq, Lückenauffüllung, 64-ms-Referenzzeit + 250-µs-Deltas mit Drift-freier Rekonstruktion) → `RtcpTransportFeedbackCodec.Encode`.
- **Senderseite:** `TransportCcCongestionController` orchestriert `TransportCcSendHistory` (direct-mapped Ring 4096), `TransportCcFeedbackInterpreter` (Arrival-Rekonstruktion), `TransportCcFeedbackCorrelator` (Delay-Gradienten `(Δarrival − Δsend)`), `TransportCcDelayTrendEstimator` (EWMA + fester 5-ms-Overuse-Threshold → `CongestionSignal`), `TransportCcLossEstimator` (EWMA), `CongestionBitrateController` (AIMD: ×0,85 bei Overuse/≥10 % Loss, +100 kbps sonst, 100 kbps–5 Mbps).
- Bewusst dokumentierte Vereinfachung gegenüber GCC (kein adaptiver Threshold/Trendline-Regressor, kein SCReAM); Upgrade-Pfad (RFC 8298) im Code notiert.

### 2.4 Krypto-Pipeline (DTLS → Key-Export → SRTP)

1. **Demux:** `MediaPacketClassifier` (RFC 7983: STUN 0–3+Magic-Cookie, DTLS 20–63, RTCP V=2 & PT 192–223, sonst RTP).
2. **Handshake:** DTLS-Records → `QueueDatagramTransport` (BlockingCollection, bounded 64) → `DtlsSrtpHandshaker` treibt den blockierenden BC-Engine auf einem `Task.Run`-Thread (`DtlsSrtpClient`/`DtlsSrtpServer`, DTLS 1.2 only, `extended_master_secret` Pflicht, `use_srtp` Pflicht, nur ECDHE-ECDSA-Suiten serverseitig). Peer-Zertifikat wird **im Handshake** per SDP-Fingerprint validiert (`DtlsFingerprintValidator`, fatale Alerts). Mutual Auth (Server fordert Client-Cert).
3. **Export:** `DtlsSrtpKeyExporter` — `EXTRACTOR-dtls_srtp`, Layout `client_key‖server_key‖client_salt‖server_salt`, Rollen-abhängige Local/Remote-Zuordnung → `DtlsSrtpNegotiatedKeys`.
4. **Kontexte:** `DtlsMediaAttachment.RunHandshakeAsync` erzeugt 4 Kontexte (Out/In × SRTP/SRTCP) plus optional 2 RTX-SRTP-Kontexte und installiert sie per Callback (`RtpSession.InstallSecurityContexts` bzw. Bundle-Pipelines). Bis dahin gilt **fail-closed** (`RequireEncryptedMedia`): kein Plaintext raus, kein Plaintext rein.
5. **SRTP:** `SrtpContext`/`SrtcpContext` pro Richtung, Session-Keys via `SrtpKeyDerivation` (AES-CM-PRF, Labels 0–2 bzw. 3–5), AES-CM-Keystream via `AesCmCipher` (wiederverwendeter ECB-Encryptor), HMAC-SHA1-Tag (verify-then-decrypt, `FixedTimeEquals`), **per-SSRC** ROC/Replay-Zustand (`SrtpSsrcState`/`SrtcpSsrcState` über `SlidingReplayWindow`, 64 Pakete) — dadurch dient *ein* Kontext allen SSRCs eines BUNDLE-Transports. Inbound-SSRC-State wird erst nach erfolgreicher Authentifizierung committet (Anti-DoS gegen SSRC-Spray). `Dispose` nullt Session-Keys (`CryptographicOperations.ZeroMemory`).

SDES-Alternative: `SdesMediaCryptoContextFactory` parst `a=crypto`-Material (`SrtpKeyMaterial.ParseInline`) und baut dieselben Kontexte; unparsbares Material bei ausgehandeltem SRTP → Exception (fail-closed).

---

## 3. Klassenkatalog

### 3.1 Application/Media/Rtcp (Modelle)

| Typ | Datei | Beschreibung |
|---|---|---|
| `RtcpCname` (static) | `Application/Media/Rtcp/RtcpCname.cs` | Erzeugt opake per-Session-CNAME (96 Bit Zufall, base64url) nach RFC 7022; nie Maschinenname (Privacy). |
| `IRtcpPacketCodec` | `Application/Media/Rtcp/Wire/IRtcpPacketCodec.cs` | Port: Decode/Encode von RTCP-**Compound**-Datagrammen. |
| `RtcpPacket` (abstract) | `Packets/RtcpPacket.cs` | Basisklasse; `Type`-Property. |
| `RtcpPacketType` (enum) | `Packets/RtcpPacketType.cs` | PT-Werte 200–207 (SR, RR, SDES, BYE, APP, RTPFB, PSFB, XR). |
| `RtcpSenderReport` | `Packets/RtcpSenderReport.cs` | SR: NTP-/RTP-Timestamp, Paket-/Oktettzähler, bis 31 Reportblöcke. |
| `RtcpReceiverReport` | `Packets/RtcpReceiverReport.cs` | RR: SSRC + Reportblöcke. |
| `RtcpReportBlock` | `Packets/RtcpReportBlock.cs` | 24-Byte-Reportblock: FractionLost, CumLost (24-Bit signed), ExtHighestSeq, Jitter, LSR, DLSR. |
| `RtcpSdesPacket` / `RtcpSdesChunk` / `RtcpSdesItem` / `RtcpSdesItemType` | `Packets/RtcpSdes*.cs` | SDES-Modell (Chunks → Items, CNAME etc.). |
| `RtcpByePacket` | `Packets/RtcpByePacket.cs` | BYE mit Quellen + optionalem Reason. |
| `RtcpPictureLossIndication` | `Packets/RtcpPictureLossIndication.cs` | PSFB FMT=1; SSRC-Paar, kein FCI. |
| `RtcpFullIntraRequest` / `RtcpFirEntry` | `Packets/RtcpFullIntraRequest.cs`, `RtcpFirEntry.cs` | PSFB FMT=4; Einträge (MediaSsrc + 8-Bit-CmdSeq). |
| `RtcpGenericNack` / `RtcpNackEntry` | `Packets/RtcpGenericNack.cs`, `RtcpNackEntry.cs` | RTPFB FMT=1; PID+BLP, `LostSequenceNumbers()` expandiert Bitmasken. |
| `RtcpTransportFeedback` / `RtcpTransportFeedbackStatus` | `Packets/RtcpTransportFeedback*.cs` | TWCC-Modell (RTPFB FMT=15): 24-Bit-Referenzzeit (64 ms), FbPktCount, per-Paket-Status (received + DeltaTicks in 250 µs). |
| `RtcpExtendedReport` / `RtcpVoipMetricsBlock` | `Packets/RtcpExtendedReport.cs`, `RtcpVoipMetricsBlock.cs` | RFC-3611-XR-Container, nur VoIP-Metrics (BT=7) modelliert. |

### 3.2 Infrastructure/Rtcp/Wire

| Typ | Datei | Beschreibung |
|---|---|---|
| `RtcpPacketCodec` | `Infrastructure/Rtcp/Wire/RtcpPacketCodec.cs` | Compound-Codec: Header/Länge/Padding-Validierung, tolerantes Überspringen unbekannter PTs (RFC 3550 §6.1), tolerantes Feedback-Decode (fehlerhafte FCIs verwerfen nur das Einzelpaket), XR-Decode mit Block-Skipping; Encode für SR/RR/SDES/BYE + Delegation an Feedback-Codec. |
| `RtcpFeedbackCodec` (static) | `Infrastructure/Rtcp/Wire/RtcpFeedbackCodec.cs` | PLI/FIR/NACK-Wire-Codec (gemeinsames Layout SenderSSRC/MediaSSRC/FCI); FIR-Encode setzt Header-MediaSSRC = 0 (RFC 5104); TWCC-Dispatch an eigenen Codec. |
| `RtcpTransportFeedbackCodec` (static) | `Infrastructure/Rtcp/Wire/RtcpTransportFeedbackCodec.cs` | TWCC-Chunk/Delta-Packing: Encode nur Two-Bit-Status-Vector-Chunks; Decode akzeptiert Run-Length-, One-Bit- und Two-Bit-Chunks, sign-extends die 24-Bit-Referenzzeit, validiert Truncation und reservierte Symbole. |

### 3.3 Infrastructure/Srtp

| Typ | Datei | Beschreibung |
|---|---|---|
| `AesCmCipher` | `Srtp/Crypto/AesCmCipher.cs` | AES-CM-Keystream-XOR (in-place), wiederverwendeter ECB-Encryptor + zwei 16-Byte-Arbeitspuffer; 2^16-Block-Limit; nicht selbst thread-safe (Owner-Lock). |
| `SrtpCryptoSuite` (enum) | `Srtp/Crypto/SrtpCryptoSuite.cs` | AES-CM-128/256 × SHA1-80/32. |
| `SrtpCryptoSuiteNames` (static) | `Srtp/Crypto/SrtpCryptoSuiteNames.cs` | RFC-4568-Token↔Suite, Key-/Salt-Längen; Default `AES_CM_128_HMAC_SHA1_80`. |
| `SrtpKeyDerivation` (static) | `Srtp/Crypto/SrtpKeyDerivation.cs` | RFC-3711-§4.3-KDF (AES-CM als PRF), Labels 0–2 (SRTP) und 3–5 (SRTCP), kdr=0. |
| `SrtpKeyMaterial` | `Srtp/Crypto/SrtpKeyMaterial.cs` | Master-Key/-Salt + Suite; `ParseInline` für RFC-4568-`inline:`-Keys (ignoriert `|`-Suffixe wie Lifetime/MKI). |
| `SrtpSessionKeys` | `Srtp/Crypto/SrtpSessionKeys.cs` | Abgeleitete Cipher-/Salt-/Auth-Keys; `Zero()` überschreibt alle Bytes. |
| `ISrtpContext` / `ISrtcpContext` | `Srtp/Context/ISrtpContext.cs`, `ISrtcpContext.cs` | Protect/Unprotect-Verträge, per Richtung, IDisposable (Zeroing). |
| `SrtpContext` | `Srtp/Context/SrtpContext.cs` | Vollständige SRTP-Engine: per-SSRC-ROC/Replay-Map, verify-then-decrypt, IV-Konstruktion (salt ⊕ SSRC·2⁶⁴ ⊕ index·2¹⁶), header-längenvalidierendes `GetRtpHeaderLength`, deferred Inbound-State-Commit erst nach Auth. |
| `SrtcpContext` | `Srtp/Context/SrtcpContext.cs` | SRTCP: 8-Byte-Klarheader, E-Flag+31-Bit-Index, HMAC über Paket+Index-Wort, per-SSRC-Index/Replay. |
| `SrtpSsrcState` / `SrtcpSsrcState` | `Srtp/Context/Srtp(Srtcp)SsrcState.cs` | Per-SSRC Sender-Index/ROC-Schätzung (libsrtp-äquivalentes signed-Delta) bzw. 31-Bit-SRTCP-Sende-Index; Replay-Delegation. |
| `SlidingReplayWindow` | `Srtp/Context/SlidingReplayWindow.cs` | 64-Bit-Bitmap-Fenster (Check/Update getrennt), ulong-Index für 48-Bit-SRTP-Index. |
| `SrtpAuthenticationException` / `SrtpReplayException` | `Srtp/Context/…Exception.cs` | Typisierte Drop-Signale für den Empfangspfad. |

### 3.4 Infrastructure/Dtls

| Typ | Datei | Beschreibung |
|---|---|---|
| `DtlsCertificate` | `Dtls/DtlsCertificate.cs` | Ephemeres selbstsigniertes ECDSA-P-256/SHA-256-Zertifikat (BC); `FromX509` für gepinnte Identitäten (nur exportierbare P-256-Keys, fail-closed); baut TLS-Chain + Signer-Credentials. |
| `DtlsFingerprint` (record) | `Dtls/DtlsFingerprint.cs` | RFC-8122-Fingerprint (sha-256, Hex-Kolon), case-insensitive `Matches`. |
| `DtlsFingerprintValidator` (static) | `Dtls/DtlsFingerprintValidator.cs` | Validiert End-Entity-Cert gegen SDP-Fingerprint; fatale Alerts (handshake_failure / unsupported_certificate / bad_certificate). |
| `DtlsRole` (enum) | `Dtls/DtlsRole.cs` | Client (setup:active) / Server (setup:passive). |
| `DtlsSrtpClient` | `Dtls/DtlsSrtpClient.cs` | DTLS-1.2-Client: bietet `use_srtp` (Profile-Liste), erzwingt genau ein gespiegeltes Profil + leeres MKI, EMS Pflicht, Key-Export in `NotifyHandshakeComplete`. |
| `DtlsSrtpClientAuthentication` | `Dtls/DtlsSrtpClientAuthentication.cs` | Fingerprint-Prüfung des Server-Certs beim Eintreffen; beantwortet CertificateRequest mit lokaler Identität. |
| `DtlsSrtpServer` | `Dtls/DtlsSrtpServer.cs` | DTLS-1.2-Server: nur ECDHE-ECDSA-Suiten (GCM zuerst), verlangt `use_srtp`, wählt Profil per lokaler Präferenz, fordert und fingerprint-prüft Client-Cert. |
| `DtlsSrtpProfiles` (static) | `Dtls/DtlsSrtpProfiles.cs` | `use_srtp`-Codepoints ↔ Suiten; nur AES128-CM-SHA1-80/-32. |
| `DtlsSrtpKeyExporter` (static) | `Dtls/DtlsSrtpKeyExporter.cs` | RFC-5764-§4.2-Export + Local/Remote-Split nach Rolle. |
| `DtlsSrtpNegotiatedKeys` | `Dtls/DtlsSrtpNegotiatedKeys.cs` | Suite + Local-/Remote-Master-Material. |
| `DtlsSrtpHandshakeResult` | `Dtls/DtlsSrtpHandshakeResult.cs` | Keys + lebender `DtlsTransport`; Dispose = close_notify (idempotent). |
| `IDtlsSrtpHandshaker` / `DtlsSrtpHandshaker` | `Dtls/IDtlsSrtpHandshaker.cs`, `DtlsSrtpHandshaker.cs` | Handshake-Treiber: BC-Engine auf Worker-Thread, Abbruch via `transport.Close()` (Cancellation-Registration), niemals unkeyed Result. |
| `DtlsSrtpHandshakeException` | `Dtls/DtlsSrtpHandshakeException.cs` | Fail-closed-Signal. |
| `DtlsMediaAttachment` | `Dtls/DtlsMediaAttachment.cs` | Brücke Socket↔Handshake: Source-Filter auf nominierten Remote (ICE-folgend via `UpdateRemoteEndPoint`), Kontext-Erzeugung + Callbacks (inkl. RTX-Sekundärkontexte), sorgfältige Dispose-Reihenfolge (close_notify vor Cancel, Handshake-Task awaiten, Kontexte disposen). |
| `QueueDatagramTransport` | `Dtls/QueueDatagramTransport.cs` | BC-`DatagramTransport` über bounded BlockingCollection (64) + Send-Callback; Timeout −1 lässt BC-Retransmit-Timer laufen; Closed+drained → ObjectDisposedException (schneller Abbruch). |

### 3.5 Infrastructure/Rtp/Packets

| Typ | Datei | Beschreibung |
|---|---|---|
| `RtpPacket` | `Rtp/Packets/RtpPacket.cs` | Immutables RTP-Modell (V/P/M/PT/Seq/TS/SSRC/CSRC/Extension/Payload als `ReadOnlyMemory`). |
| `RtpExtension` | `Rtp/Packets/RtpExtension.cs` | Profil (0xBEDE/0x1000) + Rohdaten ohne 4-Byte-Präfix. |
| `RtpHeaderExtensionElement` (record struct) | `Rtp/Packets/RtpHeaderExtensionElement.cs` | Id (1–14) + Value (1–16 B). |
| `OneByteRtpHeaderExtensions` (static) | `Rtp/Packets/OneByteRtpHeaderExtensions.cs` | RFC-8285-One-Byte-Codec: Encode (strikt), Parse (lenient), allocation-freie Spezialpfade `EncodeTransportSequenceNumber`/`TryReadTransportSequenceNumber`. |
| `RtpMidHeaderExtension` / `RtpRidHeaderExtension` (static) | `Rtp/Packets/RtpMid(Rid)HeaderExtension.cs` | MID- (RFC 9143) bzw. RID- (RFC 8852) SDES-Extension, je Encode + allocation-freies TryRead; 16-Byte-Limit der One-Byte-Form. |
| `RtpHeaderExtensionUris` (static) | `Rtp/Packets/RtpHeaderExtensionUris.cs` | Bekannte extmap-URIs (TWCC-draft-URI, MID-, RID-URN). |

### 3.6 Infrastructure/Rtp/Wire

| Typ | Datei | Beschreibung |
|---|---|---|
| `IRtpPacketCodec` / `RtpPacketCodec` | `Rtp/Wire/IRtpPacketCodec.cs`, `RtpPacketCodec.cs` | RFC-3550-§5-Codec: Version-, CSRC-, Extension-, Padding-Validierung (FormatException bei Malformed); Encode mit 4-Byte-gerundeter Extension. |

### 3.7 Infrastructure/Rtp/Session

| Typ | Datei | Beschreibung |
|---|---|---|
| `IRtpSession` | `Rtp/Session/IRtpSession.cs` | Vertrag: SendAsync, StartAsync, PacketReceived, SsrcCollisionDetected. |
| `RtpSession` | `Rtp/Session/RtpSession.cs` (978 Z.) | Herzstück des Einzelstream-Pfads: UDP-Socket, Single-Thread-Receive-Loop mit gepooltem Buffer, RFC-7983-Demux (STUN/DTLS/RTCP/RTX/RTP-Events), SRTP/SRTCP-(Un)Protect fail-closed, Symmetric-RTP-Latch, per-SSRC-Sequenzvalidierung mit LRU-Cap (64), SSRC-Kollisionsauflösung (§8.2: BYE + neue SSRC/Seq/TS unter `_sendSync`), TWCC-Stempel, Sekundärstream (RTX) mit eigenen Kontexten, Sender-Statistik-Snapshot. |
| `RtpSessionOptions` | `Rtp/Session/RtpSessionOptions.cs` | Endpunkte, PT, ClockRate, SamplesPerPacket, SRTP-Kontexte, `RequireEncryptedMedia`, TWCC-/MID-Extension-Ids. |
| `RtpOutboundHeaderExtensionStamper` | `Rtp/Session/RtpOutboundHeaderExtensionStamper.cs` | Baut per Paket die 0xBEDE-Extension aus konstanten (MID/RID, vorgebaut) + variablem TWCC-Element. |
| `RtpSequenceValidator` / `RtpSequenceResult` | `Rtp/Session/RtpSequenceValidator.cs`, `RtpSequenceResult.cs` | RFC-3550-§A.1 (MaxDropout 3000, MaxMisorder 100, **MinSequential=1** — bewusste Abweichung von RFC-Empfehlung 2, begründet mit SDP-bekannter Quelle und G.722-Decoder-State). |
| `RtpTrackedSsrc` | `Rtp/Session/RtpTrackedSsrc.cs` | Validator + LastActivity-Marker für LRU-Eviction. |
| `RtpSenderStatisticsSnapshot` (record struct) | `Rtp/Session/RtpSenderStatisticsSnapshot.cs` | SSRC, Paket-/Oktettzähler, letzter TS, HasSent. |

### 3.8 Infrastructure/Rtp/JitterBuffer, Packetisation, Retransmission

| Typ | Datei | Beschreibung |
|---|---|---|
| `IJitterBuffer` / `JitterBuffer` / `JitterBufferOptions` / `JitterBufferAddResult` | `Rtp/JitterBuffer/*.cs` | Siehe §2.2. |
| `AnnexBParser` (static) | `Rtp/Packetisation/AnnexBParser.cs` | Zero-copy NAL-Split (3-/4-Byte-Startcodes, max. 1 Trailing-Zero-Trim — dokumentierte cabac_zero_words-Grenze). |
| `H264Packetiser` / `H264Depacketiser` | `Rtp/Packetisation/H264*.cs` | RFC 6184: Single-NAL + FU-A senden; Single-NAL/STAP-A/FU-A empfangen, IDR-Erkennung (NAL 5), fail-closed Discards mit Zähler; STAP-B/MTAP/FU-B unsupported. |
| `Vp8Packetiser` / `Vp8Depacketiser` | `Rtp/Packetisation/Vp8*.cs` | RFC 7741: minimaler 1-Byte-Descriptor senden (keine PictureID); Empfang parst X/I/L/T/K-Extensions, Keyframe = P-Bit 0. |
| `IVideoPacketiser` / `IVideoDepacketiser` / `VideoRtpPayload` / `VideoPayloadFormat` | `Rtp/Packetisation/*.cs` | Verträge + Codec-Name→Paar-Factory (VP8/H264, sonst Exception). |
| `RtpRetransmissionBuffer` | `Rtp/Retransmission/RtpRetransmissionBuffer.cs` | Bounded History (Default 512, Cap 32768 wg. Seq-Aliasing) Seq→Paket, FIFO-Eviction, thread-safe. |
| `RtxPacketFactory` (static) | `Rtp/Retransmission/RtxPacketFactory.cs` | RFC-4588-§4-Encapsulate/Decapsulate (2-Byte-OSN-Präfix); Extensions/CSRC bewusst nicht übertragen. |

### 3.9 Infrastructure/Rtp (Root) — Einzelstream-Pfad

| Typ | Datei | Beschreibung |
|---|---|---|
| `RtpCallMediaSession` | `Rtp/RtpCallMediaSession.cs` (887 Z.) | `ICallMediaSession`-Implementierung: Jitter-Buffer + Playout-Loop, PLC (max. 3 Concealment-Frames, Wiederholung des letzten Payloads), DTMF senden/empfangen (RFC 4733, Start+2×Ende, Timestamp-Reservierung), Bridge-Transcoding (µ-law-Tap), Outbound-PT-Adaption an beobachtete Inbound-PT, Metriken-/RTP-Snapshots, ICE-/DTLS-Attachments, Besitz der SDES-Kontexte. |
| `RtpCallMediaSessionFactory` | `Rtp/RtpCallMediaSessionFactory.cs` | Port-Implementierung `ICallMediaSessionFactory`. |
| `VideoRtpStream` | `Rtp/VideoRtpStream.cs` (561 Z.) | Video-m-line: eigener `RtpSession`+DTLS+ICE, Packetiser/Depacketiser, RTX (NACK→Resend, Reorder-Window 32), `VideoKeyFrameFeedback`, TWCC beidseitig, `LossReport`-Klassifikation (Reorder ≠ Loss, >256 Lücke → nur PLI). |
| `VideoKeyFrameFeedback` | `Rtp/VideoKeyFrameFeedback.cs` | RFC 4585/5104: Inbound PLI/FIR → Keyframe-Callback; Inbound NACK → Retransmit-Callback; Outbound NACK (Bitmask-Gruppierung) + PLI (500-ms-Throttle), gated auf `a=rtcp-fb`. |
| `VideoReorderBuffer` | `Rtp/VideoReorderBuffer.cs` | Siehe §2.2; Contiguous-Release, Depth-Cap 16384. |
| `InboundRtpStatistics` (+`InboundCounters`, `InboundRtcpReport`) | `Rtp/InboundRtpStatistics.cs` | Lock-freie Lieferzähler + lock-geschützter RR-State (§A.1/§A.3-Bookkeeping, FractionLost-Intervall-Baseline). |
| `MediaPacketClassifier` / `MediaPacketKind` | `Rtp/MediaPacketClassifier.cs`, `MediaPacketKind.cs` | RFC-7983-Demux (pur, seiteneffektfrei). |
| `RtcpTransmissionInterval` | `Rtp/RtcpTransmissionInterval.cs` | RFC-3550-§6.2/§6.3.1: bandbreiten-/mitglieder-skaliertes, randomisiertes Intervall ([0,5;1,5], /1,21828), Sender-25 %-Split, halbes Tmin initial. |
| `RtpTelephoneEventCodec` (static) | `Rtp/RtpTelephoneEventCodec.cs` | RFC-4733-Payload-Codec + ms↔RTP-Unit-Konvertierung (40-ms-Floor). |
| `SdesMediaCryptoContextFactory` (static) | `Rtp/SdesMediaCryptoContextFactory.cs` | SDES→Kontextpaare (auch RTX-Sekundärpaar); fail-closed bei unparsbarem Material. |

### 3.10 Infrastructure/Rtp (Root) — BUNDLE-Pfad

| Typ | Datei | Beschreibung |
|---|---|---|
| `BundledMediaSession` | `Rtp/BundledMediaSession.cs` (834 Z.) | Komposition des BUNDLE-Stacks: Transport + In-/Outbound-Pipelines + DTLS-Keying + ICE + RTCP-Reporter + Stats/Quality + DTMF + TURN-Relay-Transition (ChannelBind, Keepalives, Commit-Semantik) + Simulcast-Registrierung; präzise Dispose-Reihenfolge. |
| `BundledMediaSessionBuilder` / `BundledMediaSessionFactory` / `BundledMediaSessionOptions` | `Rtp/BundledMediaSession{Builder,Factory,Options}.cs` | Mapping der Call-Parameter auf Options (DTLS-Pflicht validiert), SSRC-Erzeugung (distinct, 31-Bit), Options-Record (inkl. `PreBoundSocket`, `RelayIceBindingFactory`, `Cname`). |
| `BundledMediaTransport` (+`BundledMediaTransportOptions`) | `Rtp/BundledMediaTransport.cs` (445 Z.) | Shared Socket + Receive-Loop; zwei Relay-Modi (whole-socket ChannelData vs. per-pair Indications), Suppression vor Channel-Bind, `SendAsync`/`SendToAsync`/`SendControlAsync`/`SendUnframedAsync`; idempotentes `StartAsync`. |
| `IBundledDatagramSender` | `Rtp/IBundledDatagramSender.cs` | Send-Seam der Outbound-Pipeline. |
| `BundledInboundPipeline` | `Rtp/BundledInboundPipeline.cs` | Demux → STUN/DTLS-Events (Kopie), SRTCP/SRTP-Unprotect fail-closed mit Drop-Zählern, RTP-Decode, Reception-Stats, Router-Dispatch; wirft nie. |
| `BundledOutboundPipeline` | `Rtp/BundledOutboundPipeline.cs` | (MID,RID)-Track-Registry, fail-closed SRTP-/SRTCP-Send, SR-Snapshots, `PacketSent`-Event, Zähler. |
| `BundledOutboundTrack` | `Rtp/BundledOutboundTrack.cs` | Per-Stream Seq-/TS-Cursor unter Lock, MID/RID-Stamper, atomare SR-Zähler-Snapshots (inkl. Wanduhr-Anker für TS-Extrapolation). |
| `BundledOutboundTrackKey` (record struct) | `Rtp/BundledOutboundTrackKey.cs` | (Mid, Rid)-Routingschlüssel. |
| `BundledRtpDemultiplexer` / `BundledRtpDemultiplexerFactory` | `Rtp/BundledRtpDemultiplexer*.cs` | RFC-8843-§9.2-Assoziation: SSRC-Latch → MID-Extension → eindeutige PT; unbekannte explizite MID wird verworfen; Factory filtert mehrdeutige PTs. |
| `BundledTrackRouter` | `Rtp/BundledTrackRouter.cs` | MID→Sink-Registry + Drop-Zähler. |
| `BundledInboundReceptionStats` (+`BundledStreamKind`, `BundledInboundClockDescriptor`, `BundledInboundSourceKind`, `BundledInboundSsrcJitter`, `BundledReceptionReportBlock`) | `Rtp/BundledInboundReceptionStats.cs` | Per-SSRC-Empfangsstatistik (§A.1/§A.3/§A.8) mit PT-basierter Clock-/Kind-Auflösung, SR-LSR/DLSR-Erfassung, Report-Block- und Jitter-Snapshots. |
| `BundledSourceReceptionState` | `Rtp/BundledSourceReceptionState.cs` | Der eigentliche per-SSRC-Zustand: Seq/Cycles/Loss, §A.8-Jitter (ausgehandelte Clock, Fallback-Inferenz mit Re-Baseline bei Upgrade), DLSR-Berechnung. |
| `BundledOutboundQualityTracker` (+`BundledOutboundSsrcQuality`, `BundledMediaQuality`) | `Rtp/BundledOutboundQualityTracker.cs` | RTT aus LSR/DLSR-Echo (nur bei Match des letzten SR; negatives RTT verworfen) + Peer-Loss, per Sende-SSRC. |
| `BundledRtcpReporter` | `Rtp/BundledRtcpReporter.cs` (409 Z.) | Periodische SR/RR+SDES-Compounds (31-Block-Paging, zusätzliche RRs), SR-RTP-TS-Extrapolation auf den Reportzeitpunkt, RFC-§6.3-Intervall + Größen-EWMA, Teardown-BYE (RR+SDES+BYE). |
| `BundledStreamQuality` (+`BundledOutboundStreamIdentity`, `BundledStreamQualityAccumulator`) | `Rtp/BundledStreamQuality.cs` | Per-MID-Qualitätsfaltung (Worst-of für RTT/Loss/Jitter). |
| `BundledMediaStats` (record struct) | `Rtp/BundledMediaStats.cs` | Transportzähler-Snapshot. |
| `BundledTrackConfig` (+`BundledVideoEncoding`) | `Rtp/BundledTrackConfig.cs` | m-line-Konfiguration (MID, SSRC, PT, ClockRate, DTMF-PT/-Clock, Codec, Simulcast-Encodings). |
| `BundledVideoTrack` | `Rtp/BundledVideoTrack.cs` | Video über Bundle: Send (Single oder per-RID-Layer, Frame-atomar per Semaphore), Receive (ReorderBuffer→Depacketiser, Reset bei Diskontinuität), Frame-/Keyframe-Zähler. |
| `BundledVideoSendEncoding` | `Rtp/BundledVideoSendEncoding.cs` | Per-Layer-Packetiser + Send-Lock. |
| `BundledDtlsKeying` | `Rtp/BundledDtlsKeying.cs` | Verkabelt `DtlsMediaAttachment` mit den Bundle-Pipelines (Key-Installation beidseitig, `onKeysInstalled` → `Connected`). |
| `BundledIceControl` | `Rtp/BundledIceControl.cs` | Verkabelt `IceMediaAttachment` (Consent, Nomination, Trickle, Relay-Kandidaten) mit dem Bundle. |
| `BundledCallMediaSession` | `Rtp/BundledCallMediaSession.cs` | Adapter Bundle→`ICallMediaSession`; DTMF/RTCP-Mux/Metriken/Video bewusst `NotSupported`/leer (FOLLOW-UPs dokumentiert). |
| `BundledSenderReportInfo` (record struct) | `Rtp/BundledSenderReportInfo.cs` | SR-Zähler-Snapshot inkl. TS-Extrapolationsanker. |

---

## 4. Zentrale Abläufe / Datenflüsse

### 4.1 RTP senden (Einzelstream)
`RtpCallMediaSession.SendFrameAsync` → (opt. Bridge-Transcode) → `RtpSession.SendCoreAsync`: unter `_sendSync` werden SSRC/Seq/TS/TWCC-Seq konsistent alloziert → `RtpPacket` mit Extension-Stempel → `RtpPacketCodec.Encode` → unter `_srtpProtectSync` `SrtpContext.Protect` (ROC-Schätzung aus Seq, AES-CM über Payload, HMAC über Paket‖ROC, Tag angehängt) → `UdpClient.SendAsync` an gelatchten/negotiierten Remote → Zähler, `PacketSent`-Event (→ RTX-Buffer, TWCC-SendHistory). Fail-closed: ohne Kontext bei `RequireEncryptedMedia` wird unterdrückt.

### 4.2 RTP empfangen (Einzelstream)
Receive-Loop (1 Thread, gepoolter Buffer) → `MediaPacketClassifier` → STUN/DTLS als Kopien an ICE/DTLS-Attachment; RTCP → SRTCP-Unprotect (fail-closed, alle Fehlerklassen = sauberer Drop) → `ControlPacketReceived`; RTX-PT → Sekundärkontext → `SecondaryPacketReceived`; sonst SRTP-Unprotect (Auth→Replay-Check→Decrypt→Window-Update) → `RtpPacketCodec.Decode` → Symmetric-Latch → SSRC-Kollisionsprüfung → §A.1-Validator (LRU-Cap 64) → `PacketReceived` → `RtpCallMediaSession.OnPacketReceived`: DTMF-Abzweig oder `JitterBuffer.Add`; Playout-Loop zieht per `TryGetNext`, füllt Lücken mit bis zu 3 Concealment-Frames, dispatcht `FrameReceived`.

### 4.3 RTCP-Compound-Verarbeitung
- **Einzelstream:** entschlüsseltes Datagramm wird roh an `RtcpMuxDatagramReceived` (SIP-Monitor außerhalb des Bereichs) durchgereicht; im Video-Pfad decodieren `VideoKeyFrameFeedback` und `TransportCcCongestionController` **jeweils separat** dasselbe Compound.
- **Bundle:** `BundledInboundPipeline.ProcessRtcp` → `BundledMediaSession.OnControlPacketReceived` decodiert einmal: SRs → `RecordSenderReport` (LSR/DLSR-Basis) + Reportblöcke → `BundledOutboundQualityTracker` (RTT = arrival − sentAt − DLSR, nur bei LSR-Match, negatives verworfen). Ausgehend baut `BundledRtcpReporter` SR(s)/RR + SDES(CNAME), mit 31er-Paging und §6.3-Intervall; beim Dispose ein BYE.

### 4.4 NACK/RTX
Empfänger (`VideoRtpStream.OnPacketReceived`): Vorwärtslücke (2–256) → sofortiger `RtcpGenericNack` (Bitmask-Gruppierung) + gedrosselter PLI; Reorder (Rückwärtsschritt) wird unterdrückt. Sender: NACK → `VideoKeyFrameFeedback.OnControlDatagram` → `OnRetransmitRequested` → `RtpRetransmissionBuffer.TryGet` → `RtxPacketFactory.Encapsulate` (RTX-PT/-SSRC, frische RTX-Seq, OSN-Präfix) → `RtpSession.SendSecondaryAsync` (eigener SRTP-Kontext → eigenes Replay-Window). Empfangsseitig wird der RTX per PT demultiplext, decapsuliert und in den `VideoReorderBuffer` eingespeist, wo er seine Lücke vor der Depacketisierung füllt.

### 4.5 Bandbreitenschätzung (TWCC)
Sender stempelt monoton wachsende Transport-Seq (RFC 8285) → `TransportCcSendHistory`. Empfänger zeichnet (Seq, monotone Ankunft) auf und sendet ~alle 100 ms ein Feedback (Builder: Unwrap, Lücken=not-received, 64-ms-Basis + 250-µs-Deltas). Sender: Interpreter rekonstruiert Ankunftszeiten → Correlator bildet Delay-Gradienten gegen Sendezeiten → EWMA-Trend → `CongestionSignal` + Loss-EWMA → AIMD-Controller → `RecommendedBitrateBps`/`NetworkQuality` + Event; die App stellt ihren Encoder darauf ein.

### 4.6 SRTP-Schutz/Entschutz inkl. Replay/ROC
Siehe §2.4; wesentlich: Index-Schätzung per signed-16-Bit-Delta gegen `HighestIndex` des Replay-Fensters (empfangsseitig) bzw. gegen den Sender-Index (sendeseitig); ROC = Index≫16 fließt nur in HMAC und IV, nie auf den Draht; Replay-`Check` vor, `Update` nach Erfolg; SRTCP nutzt stattdessen den expliziten 31-Bit-Index (kein ROC).

### 4.7 DTLS-Handshake
Start (`Start`) → verlinkte CTS → `HandshakeAsync(role, QueueDatagramTransport, cert, fingerprint)`; Records fließen: Socket-Loop → `OnDtlsPacketReceived` (Source-Filter = nominierter Remote) → Queue → blockierender BC-Engine-Thread; ausgehend synchron → fire-and-forget-Send-Bridge. Erfolg → Key-Export → 4(+2)-Kontexte → Installation → Medien fließen. Fehler → `DtlsSrtpHandshakeException` → `onHandshakeFailed` (Sendestopp, Session bleibt fail-closed). Teardown → close_notify vor Cancel, Task-Await, Kontexte + Keys zeroed.

---

## 5. Threading-, Speicher- und Fehlerbehandlungsmodell

**Threading:**
- Ein Receive-Loop-Thread pro Socket (Einzelstream und Bundle); alle Depacketiser-/DTMF-/Reorder-Zustände sind darauf single-consumer und bewusst unsynchronisiert (mehrfach explizit kommentiert).
- `SrtpContext`/`SrtcpContext`: ein Monitor pro Kontext serialisiert alle Protect/Unprotect (per-SSRC-States sind unsynchronisiert, owner-locked). Auf dem Bundle serialisiert damit **ein** Lock alle Tracks einer Richtung.
- `RtpSession`: `_sendSync` (Seq/TS/SSRC-Konsistenz), `_srtpProtectSync` (ROC-Ordnung), Volatile für Kontexte/Latch/SSRC; Zähler per `Interlocked`.
- Bundle: `ConcurrentDictionary` für Track-/SSRC-Registries, per-Track-Lock für Cursor, `Interlocked`-Zähler, Volatile-publizierte Kontexte, per-Encoding-`SemaphoreSlim` für Frame-Atomarität; Relay-Übergänge mit Interlocked-Gates (`_relayWired`, `_relayTransitionStarted`, `_relayTransitioned`).
- DTLS: eigener Worker-Thread (BC blockierend), Cancellation nur über `transport.Close()`.

**Zeitquellen:** monotone `Stopwatch`-Ticks für TWCC und PLI-Throttle; **Wanduhr (`DateTimeOffset.UtcNow`)** für Jitter-Buffer-Scheduling, §A.8-Jitter (Bundle), RTT-Ableitung, SR-NTP — injizierbar (`utcNow`-Funcs) im Bundle-Pfad für Tests.

**Speicher:** ein `ArrayPool`-Buffer pro Receive-Loop (jeder retained Byte wird kopiert); `stackalloc` für IVs/Tags; `GC.AllocateUninitializedArray` für SRTP-Ausgaben; allocation-freie Extension-Scans; bounded Strukturen überall (Replay 64, DTLS-Queue 64, SSRC-Validator-Cap 64+LRU, RTX-Buffer 512, TWCC-Ringe 1024/4096, ReorderBuffer-Depth). Erkannte Rest-Allokationen sind als FOLLOW-UP markiert (`Rtp/Session/RtpSession.cs:925-929`).

**Fehlerbehandlung:** durchgängiges Prinzip „ein feindliches/kaputtes Datagramm darf nie den Loop töten“: alle Unprotect-/Decode-Pfade fangen `SrtpAuthenticationException`, `SrtpReplayException`, `ArgumentException`, `CryptographicException`, `ObjectDisposedException` als gezählten Debug-Drop. Alle Event-Dispatches sind try/catch-isoliert. Fail-closed-Suppression statt Plaintext auf jeder Sende-/Empfangsstelle. Dispose-Reihenfolgen sind sorgfältig dokumentiert (Sendestopp → close_notify → Socket → Key-Zeroing; Bundle: ICE → Relay-Transition drain → Rebind → Keepalive → Reporter(BYE) → DTLS → Video → Transport).

---

## 6. Qualitätsbefunde

### Stärken
- Sehr hohe RFC-Trace-Dichte in Kommentaren; Abweichungen sind fast immer als bewusste Entscheidung dokumentiert (z. B. MinSequential=1, FIR nicht generiert, Vereinfachte BWE).
- Konsequentes Fail-Closed-Design über alle Keying-Pfade; verify-then-decrypt, `FixedTimeEquals`, Key-Zeroing, Anti-DoS beim Inbound-SSRC-State.
- Per-SSRC-SRTP-Kryptozustand macht die BUNDLE-Mehrstromfähigkeit korrekt (ROC/Replay nicht vermischt).
- Robuste Wire-Codecs mit Längentyp-Validierung an jeder Stelle; toleranter Compound-Decode gem. RFC 3550 §6.1.
- RTX-Design korrekt (eigene SSRC/Seq/Kontexte, OSN, Reorder-Fenster mit Contiguous-Release).
- Testbarkeit: injizierbare Clocks/Delays/Randomquellen, viele interne Test-Seams.

### Potenzielle Bugs / Risiken

1. **SRTCP-Auth-Tag-Länge bei `*_HMAC_SHA1_32`-Suiten** — `Srtp/Context/SrtcpContext.cs:43-45` setzt für die _32-Suiten auch für **SRTCP** ein 4-Byte-Tag. RFC 4568 §6.2 (und die Fußnote zu RFC 5764 §4.1.2) verlangen für SRTCP weiterhin 80 Bit; ein standardkonformer Peer (z. B. libsrtp) berechnet 10-Byte-Tags → Interop-Bruch, sobald `AES_CM_128_HMAC_SHA1_32` ausgehandelt wird. Für SRTP ist 4 Byte korrekt; die Unterscheidung fehlt nur für SRTCP.
2. **Symmetric-Latch re-latcht bei jedem abweichenden Absender** — `Rtp/Session/RtpSession.cs:645-651`: bei unverschlüsselten (Plain-RTP-)Legs kann jedes valide dekodierbare RTP-Paket eines Off-Path-Angreifers das Sende-Ziel umlenken (Media-Hijack/Reflection); mit SRTP ist der Latch authentifiziert und unkritisch. Ein „nur erste Quelle latchen“ oder Latch-Bindung an authentifizierte Pakete für Plain-Legs wäre robuster.
3. **Kernel-Socket-Puffer 8 KB** — `Rtp/Session/RtpSession.cs:90,160` und `Rtp/BundledMediaTransport.cs:35,103`: `Client.ReceiveBufferSize = 8192` setzt SO_RCVBUF auf 8 KB. Für Video-Bitraten (bis 5 Mbps laut BWE-Ceiling) ist das sehr knapp — schon kurze Verarbeitungspausen (GC, SRTP eines großen Frames) verursachen Kernel-Drops. Der Wert scheint mit der User-Space-Puffergröße (max. Datagramm) verwechselt.
4. **SSRC/Seq-Zufall nicht kryptographisch und nur 31 Bit** — `Rtp/Session/RtpSession.cs:148,156-157` (`Random.Shared.Next()`; SSRC ist nie ≥ 0x80000000, Seq nutzt `Next(65535)` — der Wert 65535 selbst ist unerreichbar) und `Rtp/BundledMediaSessionFactory.cs:50-59`. RFC 3550 empfiehlt unvorhersehbare Werte; unter SRTP nur geringes Risiko, bei Plain-RTP erleichtert es Spoofing/Kollisionen.
5. **Deterministische Startwerte im Bundle** — `Rtp/BundledMediaSessionBuilder.cs:47-48`: Default `initialSequenceNumber=1`, `initialTimestamp=0` für alle Tracks (RFC 3550 §5.1 empfiehlt Zufall; unter DTLS-SRTP praktisch unkritisch, aber eine dokumentierenswerte Abweichung).
6. **`RtpSession.StartAsync` ist nicht idempotent** — `Rtp/Session/RtpSession.cs:169-178`: ein zweiter Aufruf ersetzt `_loopCts`/`_receiveLoop` und verwaist den ersten Loop; der Bundle-Transport hat dafür extra einen Guard (HARD-C5, `BundledMediaTransport.cs:203-215`), der Einzelstream nicht.
7. **SDES-Item-Längen-Überlauf beim Encode** — `Infrastructure/Rtcp/Wire/RtcpPacketCodec.cs:441` (`buf[offset++] = (byte)valueBytes.Length;`) und analog BYE-Reason `RtcpPacketCodec.cs:474`: Werte > 255 Bytes würden still zu einem korrupten Längenbyte gecastet. Aktuelle Aufrufer (16-Zeichen-CNAME) sind sicher; latenter Encoder-Bug.
8. **Dreifaches RTCP-Decode auf dem Video-Pfad** — `Rtp/VideoRtpStream.cs:210-236`: `VideoKeyFrameFeedback`, `TransportCcCongestionController` (und ggf. weitere Abonnenten) parsen dasselbe Compound unabhängig. Funktional korrekt, aber unnötige Arbeit pro RTCP-Datagramm.
9. **NACK ohne Reorder-Toleranzfenster** — `Rtp/VideoRtpStream.cs:397-405,546-559`: eine Vorwärtslücke löst sofort NACK+PLI aus; trifft das „fehlende“ Paket eine Zeile später ein (einfaches Reordering), war der NACK überflüssig (der Peer sendet unnötig RTX). Üblich wäre eine kleine Wartezeit oder Dedup im NACK-Pfad.
10. **Kein Feedback-/Keyframe-Pfad im Bundle** — `Rtp/BundledMediaSession.cs:362-390` konsumiert nur SR/RR; PLI/FIR/NACK/TWCC werden auf dem BUNDLE-Pfad weder gesendet noch beantwortet, `BundledVideoTrack` hat kein RTX. Bei Verlust bleibt nur Depacketiser-Reset (Frame-Verwurf) bis zum nächsten Keyframe. Teilweise als FOLLOW-UP markiert (`BundledCallMediaSession.cs:64-66,98-105`).
11. **Wanduhr statt Monotonik im Jitter-Buffer und RTT** — `Rtp/JitterBuffer/JitterBuffer.cs:262` (`arrivalTime.ToUnixTimeMilliseconds()`), `Rtp/BundledOutboundQualityTracker.cs:79-84`: NTP-Sprünge verfälschen Playout-Scheduling/Jitter/RTT (negatives RTT wird immerhin verworfen). `Stopwatch`-basierte Zeit wäre robuster.
12. **TWCC-Feedback ohne Timer-Fallback** — `Rtp/CongestionControl/TransportCcFeedbackSender.cs:77-100`: rein paketgetriggert; endet der Medienfluss abrupt, wird der letzte Batch nie gemeldet (der Sender sieht die letzten Verluste nicht). Geringfügig, da BWE dann ohnehin irrelevant wird.
13. **Master-Keys aus DTLS-Export werden nicht genullt** — `Dtls/DtlsSrtpKeyExporter.cs:33-49`: das exportierte `material`-Array (und die `SrtpKeyMaterial`-Memories) verbleibt bis zur GC im Heap; `SrtpSessionKeys.Zero()` (`Srtp/Crypto/SrtpSessionKeys.cs:20-31`) deckt nur die abgeleiteten Keys ab — bei SDES dokumentiert-bewusst, für den DTLS-Pfad wäre Zeroing des Exportblocks möglich.

### Krypto-Auffälligkeiten (zusammengefasst)
- Nur AES-CM + HMAC-SHA1; keine AEAD-Profile (RFC 7714 AES-GCM) und kein DTLS 1.3 — Interop mit modernen Endpunkten funktioniert (SHA1-80 ist WebRTC-Pflichtprofil), aber kein bevorzugtes GCM.
- Kein MKI, keyderivation-rate 0, kein Re-Keying über Sessionlaufzeit — für Session-gebundene ephemere Keys akzeptabel.
- Fingerprint-Prüfung nur SHA-256 (fail-closed bei anderen Algorithmen) — konform mit RFC 8122-Empfehlung, lehnt aber Peers ab, die nur z. B. sha-384 signalisieren.
- Positive Punkte: EMS-Zwang (Triple-Handshake-Härtung), MKI-Echo-Prüfung im Client (`DtlsSrtpClient.cs:66-69`), Konstantzeitvergleich der Tags, per-SSRC-Replay auch für SRTCP.

### RFC-Abweichungen (bewusst/dokumentiert)
- `MinSequential=1` statt 2 (`Rtp/Session/RtpSequenceValidator.cs:12-16`) — begründet.
- FIR wird empfangen, aber nie generiert (`Rtp/VideoKeyFrameFeedback.cs:14-16`).
- TWCC-Encoder emittiert nur Two-Bit-Vector-Chunks (kein Run-Length) — zulässig, nur weniger kompakt.
- Vp8Packetiser ohne PictureID (`Rtp/Packetisation/Vp8Packetiser.cs:4-8`) — kann bei manchen Empfängern die Verlustdetektion schwächen; als Follow-up notiert.
- RTX-Recovered-Pakete verlieren Header-Extensions/CSRC (`Rtp/Retransmission/RtxPacketFactory.cs:22-25`) — dokumentierte Entscheidung.

### TODOs / FOLLOW-UPs im Code
- `Rtp/Packets/RtpHeaderExtensionUris.cs:15` — zusätzliche/registrierte TWCC-URIs (RFC 8888-URN) akzeptieren.
- `Rtp/Session/RtpSession.cs:928` — Pooling der ~2 Heap-Objekte pro gestempeltem Paket.
- `Rtp/BundledCallMediaSession.cs:64-66,98-99,104-106` — DTMF, RTCP-Mux-Send, Video-Exposure auf dem Bundle-Adapter.
- `Rtp/CongestionControl/CongestionBitrateController.cs:11-14` u. `TransportCcDelayTrendEstimator.cs:11-15` — Upgrade auf adaptiven Threshold/SCReAM (RFC 8298).
- **Veralteter Doc-Kommentar:** `Infrastructure/Rtcp/Wire/RtcpTransportFeedbackCodec.cs:16-19` behauptet, der Codec sei „not yet wired into the RTCP receive/dispatch path“ — tatsächlich ist er über `RtcpFeedbackCodec.Decode` (FMT 15, `RtcpFeedbackCodec.cs:42-44`) und `TransportCcCongestionController.OnControlDatagram` voll verdrahtet.
- **Totes Codefragment:** `Infrastructure/Rtcp/Wire/RtcpPacketCodec.cs:205` — ungenutzte `chunkStart`-Berechnung im SDES-Decode (durch die simplere Alignment-Schleife ersetzt, Rest stehengeblieben).

---

**Gesamteinschätzung:** Ein ungewöhnlich sorgfältig dokumentierter, defensiv gebauter Medien-Stack mit sauberer Schichtung und korrekt umgesetzten Kernprotokollen. Die gravierendste konkrete Korrektheitsfrage ist die SRTCP-Tag-Länge unter den `*_32`-Suiten (Befund 1); die relevantesten Betriebsrisiken sind der 8-KB-Kernelpuffer (Befund 3) und das Plain-RTP-Latch-Verhalten (Befund 2). Der BUNDLE-Pfad ist transportseitig komplett, aber im Feedback-/Reparatur-Bereich (PLI/NACK/RTX/TWCC) noch deutlich hinter dem Einzelstream-Pfad zurück.



---

# Teil 3 — Konnektivität: STUN/TURN/ICE (176 Dateien)


## 1. Überblick & Verantwortung

Der analysierte Bereich implementiert den vollständigen NAT-Traversal- und Relay-Stack eines C#/.NET-VoIP-SDK — **sowohl Client- als auch Server-Seite**, konsequent nach dem Prinzip „STUN als Wire-Fundament, TURN als isoliertes Protokollmodul darauf, ICE als Anwendungslogik".

**RFC-Abdeckung (explizit im Code referenziert):**
- **RFC 5389 / 8489 (STUN):** Message-Format, Magic-Cookie, XOR-MAPPED-ADDRESS, MESSAGE-INTEGRITY (HMAC-SHA1), FINGERPRINT (CRC32), Short-Term/Long-Term-Credentials (§10.1/§10.2), 401/438-Challenge-Flow, 300 Try Alternate (§11), UDP-Retransmission-Schedule (§7.2.1), TCP/TLS-Framing (§7.2.2), DNS-SRV-Discovery (§9).
- **RFC 7635 (STUN Third-Party Authorization / OAuth):** ACCESS-TOKEN, THIRD-PARTY-AUTHORIZATION, AES-256-GCM-Token-Validierung.
- **RFC 5766 / 8656 (TURN):** Allocate, Refresh, CreatePermission, ChannelBind, Send/Data-Indication, ChannelData-Framing (§11.6), EVEN-PORT/RESERVATION-TOKEN (§7), DONT-FRAGMENT, REQUESTED-ADDRESS-FAMILY, Permission-Keying nach IP (§8/§9), Channel-Keying nach IP:Port (§11), Lifetime-Management (§3.9).
- **RFC 6062 (TURN-TCP):** Connect, ConnectionBind, ConnectionAttempt, persistente TCP-Datenverbindungen.
- **RFC 8016 (TURN Mobility):** MOBILITY-TICKET, Allokations-Migration.
- **RFC 6156:** implizit über IPv6-Adressfamilien-Unterstützung (REQUESTED-ADDRESS-FAMILY, Dual-Mode-Sockets); kein eigener ADDITIONAL-ADDRESS-FAMILY-Pfad implementiert (Attributcode ist in `TurnAttributeType` definiert, aber ungenutzt).
- **RFC 8445 (ICE):** Kandidaten-Priorität (§5.1.2), Rollen/Tie-Breaker (§5.2/§7.3.1.1), Check-Listen (§6.1.2), Pair-Priority (§6.1.2.3), Foundation-Freezing (§6.1.2.6/§7.2.5.3.3), Connectivity-Checks (§7.2.2), Nomination via USE-CANDIDATE (§8.1.1), Peer-Reflexive/Triggered Checks (§7.3.1.4), Rollenkonflikt (§7.3.1.1).
- **RFC 7675 (Consent Freshness):** periodische Consent-Checks, 30-s-Consent-Lifetime, Ta-Pacing.
- **RFC 8838 (Trickle ICE):** dynamisches Nachreichen von Remote-Kandidaten (`AddRemoteCandidate`/`AddCandidate`).

## 2. Architektur & Schichtung

Vier saubere Schichten, mit strikter Modul-Isolation:

**a) Wire-Codec (STUN/Wire, TURN/Wire).** `StunMessageCodec` ist der einzige Ort für Byte-Serialisierung: Type-Word-Bit-Layout (M/C-Bits), Big-Endian, MESSAGE-INTEGRITY-Berechnung mit Längenfeld-Anpassung, FINGERPRINT-CRC32. TURN nutzt bewusst *keinen* eigenen Codec, sondern kodiert seine Attribute als `UnknownRawAttribute` über den `TurnAttributeMapper` — dadurch bleibt die TURN-Attributcode-Kenntnis komplett im TURN-Modul und die STUN-Schicht protokoll-agnostisch. `StunTcpFramer` / `TurnStreamFramer` liefern selbst-delimitierendes Stream-Framing (STUN via Längenfeld; TURN unterscheidet zusätzlich ChannelData 0x4000–0x7FFF).

**b) Messages / Attributes.** Unveränderliches Nachrichtenmodell (`StunMessage`, `StunMessageClass`, `StunMessageMethod`) plus typisierte Attribut-Klassenhierarchie (`StunAttribute`-Basis mit `UnknownRawAttribute`-Passthrough). TURN-Attribute sind eigene POCOs, die über den Mapper zu Rohattributen werden.

**c) Client/Server-Logik.** STUN-Client (`StunClient`) implementiert Retransmission + Auth-Flow + Redirect; STUN-Server (`StunServer` + `StunBindingRequestHandler`) verarbeitet Binding-Requests mit optionaler Authentifizierung und RFC-7635-Third-Party-Auth. TURN spiegelt das: `TurnClient` (Ein-Socket-pro-Transaktion) und `TurnRelayControlClient`/`TurnControlTransactor` (geteilter BUNDLE-Socket). Der TURN-Server (`TurnServer`) ist in fokussierte Kollaboratoren zerlegt (Registry, Auth, Handler pro Methode, Mobility, TCP-Broker, Reservierungs-Store).

**d) ICE-Agent-Design.** Zweigeteilt: **Anwendungslogik** (`Core/Application/Media/Ice`) enthält reine, testbare Entscheidungslogik (Check-List-Bildung, Prioritäten, Rollenkonflikt, Consent-Timing, Restart-Erkennung); **Infrastruktur** (`Core/Infrastructure/Stun/Ice`) verdrahtet diese an die STUN-Wire-Schicht und den Media-Socket. Kern ist `IceMediaAttachment`, das pro Media-Leg drei Verantwortlichkeiten bündelt: eingehende Checks beantworten (`IceInboundStunHandler`), Consent-Freshness fahren (`IceMediaConsentSession`) und — nur als Controlling-Agent — Kandidatenpaare prüfen/nominieren (`IceNominationDriver`). Bemerkenswert: der `IceNominationDriver` ist **kein** klassischer Frozen/Waiting-Scheduler, sondern eine langlebige, dynamische Check-Liste, die über den *geteilten Media-Socket* läuft und Relay-Kandidaten (TURN-gerahmt) gleichwertig einreiht.

**e) Hosting-Schicht (Client/Hosting).** Level-2-Fassaden (`StunServerHost`, `TurnServerHost`) verbergen die internen Server hinter schlanken Interfaces (`IStunServerHost`/`ITurnServerHost`), plus Level-3-DI-Integration (`AddCalloraStunServer`/`AddCalloraTurnServer` mit Fluent-Buildern und Options-Klassen). Socket wird im Konstruktor gebunden → `LocalEndPoint` sofort gültig (wichtig bei Ephemeral-Bind).

## 3. Klassenkatalog

### STUN/Wire
- **`IStunMessageCodec`** (`Wire/IStunMessageCodec.cs`) — Interface: Encode/Decode, IsStunPacket, VerifyIntegrity/VerifyFingerprint.
- **`StunMessageCodec`** (`Wire/StunMessageCodec.cs`) — Konkrete Wire-Serialisierung; Type-Word-Bitlayout, Attribut-Dispatch, HMAC/CRC mit Längenfeld-Anpassung; `MaxAttributesPerMessage=64` als Flood-Schutz.
- **`StunTcpFramer`** (`Wire/StunTcpFramer.cs`) — Liest ein komplettes STUN-Msg aus TCP-Stream über Längenfeld; Limit 65535.
- **`StunWireConstants`** (`Wire/StunWireConstants.cs`) — Magic-Cookie 0x2112A442, Header-Größe 20, Fingerprint-XOR 0x5354554E etc.

### STUN/Messages
- **`StunMessage`** — unveränderliches Msg-Modell mit Factory-Methoden für Binding-Request/Response.
- **`StunMessageClass`** (enum) — Request/Indication/Success/Error als kombiniertes C1/C0-Bitmuster (0x0110).
- **`StunMessageMethod`** (enum) — nur `Binding = 0x0001`; TURN-Methoden werden separat gecastet.

### STUN/Attributes (17 Klassen + 2 enums)
`StunAttribute` (abstrakte Basis), `StunAttributeType` (enum, Codes), `MappedAddressAttribute`, `XorMappedAddressAttribute`, `AlternateServerAttribute`, `UsernameAttribute`, `RealmAttribute`, `NonceAttribute`, `MessageIntegrityAttribute`, `FingerprintAttribute`, `ErrorCodeAttribute`, `UnknownAttributesAttribute`, `UnknownRawAttribute` (Passthrough, bewahrt Roh-Typcode), `SoftwareAttribute`, `PriorityAttribute`, `UseCandidateAttribute`, `IceControllingAttribute`/`IceControlledAttribute` (64-Bit-Tie-Breaker), `ChangeRequestAttribute` (RFC-3489-Legacy, nur zum expliziten Ablehnen dekodiert), `AccessTokenAttribute`, `ThirdPartyAuthorizationAttribute` (RFC 7635). Alle mit sauberer 1-Satz-XML-Doku und RFC-Verweis.

### STUN/Auth
- **`StunCredentials`** — Short-/Long-Term-Credential-Set; `DeriveHmacKey()`, `WithRealmAndNonce`/`WithNonce` für Challenge-Flow.
- **`StunKeyDerivation`** — Short-Term-Key = SASLprep(pwd) UTF-8; Long-Term = MD5(user:realm:SASLprep(pwd)); enthält eine **eigene SASLprep-Teilimplementierung** (RFC 4013), bewusst *ohne* Bidi-Checks.

### STUN/Client
- **`IStunClient`**/**`StunClient`** — Binding über UDP/TCP/TLS; UDP-Retransmission (RTO 500 ms, ×2, Cap 16 s, 7 Versuche); Long-Term-Auth-Flow (unauth → 401 → 438); 300-Redirect (einmalig). Unterstützt geteilten UDP-Socket (für ICE-Gathering auf Media-Port) und zusätzliche Attribute (ICE-Checks).
- **`StunTransport`** (enum), **`StunBindingResult`**, **`StunException`**, **`StunChallengeException`** (intern für 401/438).
- **`IStunServerResolver`**/**`StunServerResolver`** — DNS-SRV mit A/AAAA-Fallback, RFC-2782-Gewichtsauswahl.
- **`DnsSrvQuery`** — rohe UDP-DNS-Abfrage (SRV), inkl. Name-Compression-Parsing; `GetSystemDnsServer()` aus `/etc/resolv.conf`, Fallback 8.8.8.8.
- **`DnsSrvRecord`** (record).
- **`StunIceCheckAttributes`** — baut PRIORITY + ICE-CONTROLLING/CONTROLLED + optional USE-CANDIDATE.
- **`StunIceProbe`** (`IIceStunProbe`-Adapter) — srflx-Discovery + `TryCheckConnectivityAsync` für ICE; `PickAddressForFamily` verhindert IPv4/IPv6-Familien-Mismatch.

### STUN/Ice (Infrastruktur-Verdrahtung)
- **`IceConsentCheckBuilder`** — baut RFC-7675-Consent-Check (= ICE-Check ohne USE-CANDIDATE), MESSAGE-INTEGRITY mit Peer-Passwort.
- **`IceInboundBindingRequest`** (record struct) / **`IceInboundBindingResponder`** — dekodiert eingehende Check-Requests, verifiziert Integrität mit *lokalem* Passwort, baut Success (XOR-MAPPED-ADDRESS) bzw. 487-Response.
- **`IceInboundProcessingResult`** (record struct) / **`IceInboundCheckProcessor`** — verbindet Wire-Responder mit App-`IceInboundCheckEvaluator`; verwirft bei Auth-Fehler stillschweigend (Amplification-Schutz).
- **`IceInboundStunHandler`** + **`IceInboundStunHandlerFactory`** — treibt eingehende Checks auf Media-Leg, verwaltet Rollenwechsel (lock-frei via `Volatile`), Events `PairNominated`/`CheckAccepted`.
- **`IceLocalCandidate`** / **`IceRemoteCandidate`** (record struct) — Kandidaten mit Send-Path-Delegate bzw. Endpoint+Priorität.
- **`IceMediaAttachment`** — zentrale Bündelung (siehe §2d), inkl. Relay-Kandidaten-Einreihung und Consent-Umleitung.
- **`IceMediaConsentSession`** + **`IceMediaConsentSessionFactory`** — RFC-7675-Loop über Media-Socket, mit `IceStunTransactionRegistry`-Matching.
- **`IceMediaParameters`** (record) — minimale ICE-Sicht pro 5-Tupel; `FromCall`/`FromVideo`.
- **`IceNominatedTarget`** (record) — atomar publizierter (Remote, Send-Path)-Snapshot.
- **`IceNominationDriver`** + **`IceNominationPairState`** — Controlling-Agent-Nomination-Loop (siehe §4).
- **`IceStunTransactionRegistry`** — synchrone Registrierung vor Send, TCS-Matching per Transaction-ID.

### STUN/Server
- **`IStunRequestHandler`**/**`StunBindingRequestHandler`** — RFC-5389-§7.3.1-Verarbeitung; Fingerprint-Prüfung, 420-Unknown-Attribute, Auth (Short/Long-Term + Third-Party), warnt bei Konstruktion ohne Auth.
- **`StunServer`** + **`StunServerTransport`**/**`StunServerOptions`**/**`StunConnectionCapPolicy`** — UDP/TCP/TLS-Listener mit Backpressure/RejectNew-Slots.
- **`StunRequestHandlingResult`** — Response + optionaler Per-Response-Integritätsschlüssel.
- Auth-Provider: **`IStunCredentialProvider`**/**`InMemoryStunCredentialProvider`**, **`IStunNonceManager`**/**`StunNonceManager`** (128-Bit-Nonces, TTL 5 min, lazy Purge), **`IStunAccessTokenValidator`**/**`Rfc7635AccessTokenValidator`**(+Options), **`IStunThirdPartyKeyProvider`**/**`InMemoryStunThirdPartyKeyProvider`**, **`StunThirdPartyKeyMaterial`**, **`StunThirdPartyTokenEncryptionAlgorithm`**(enum), **`StunThirdPartyAuthorizationOptions`**.

### TURN/Wire & Attributes
- **`TurnAttributeType`** / **`TurnMessageMethod`** (enums), **`TurnAddressFamily`**, **`TurnRequestedTransportProtocol`** (UDP 0x11 / TCP 0x06).
- **`TurnAttributeMapper`** — Encode/Decode aller TURN-Attribute ↔ `UnknownRawAttribute`.
- **`TurnWireAddressCodec`** — XOR-PEER/RELAYED-ADDRESS-Wert-Kodierung (IPv4/IPv6 mit Transaction-ID-XOR).
- **`TurnChannelDataCodec`** — ChannelData-Framing (0x4000–0x7FFF, Längenfeld).
- **`TurnStreamFramer`**/**`TurnStreamFrame`** — Stream-Framing (STUN capped 16 KiB, ChannelData bis 64 KiB).
- Attribut-POCOs: `TurnChannelNumberAttribute`, `TurnLifetimeAttribute`, `TurnXorPeerAddressAttribute`, `TurnDataAttribute`, `TurnXorRelayedAddressAttribute`, `TurnRequestedAddressFamilyAttribute`, `TurnEvenPortAttribute`, `TurnRequestedTransportAttribute`, `TurnDontFragmentAttribute`, `TurnReservationTokenAttribute`, `TurnConnectionIdAttribute`, `TurnMobilityTicketAttribute`.

### TURN/Client
- **`ITurnClient`**/**`TurnClient`** — vollständige Methoden-Palette (Allocate/Refresh/CreatePermission/ChannelBind/Connect/ConnectionBind/OpenTcpDataConnection/SendIndication); validiert widersprüchliche Options (RAF+RESERVATION-TOKEN; TCP+DONT-FRAGMENT/EVEN-PORT).
- **`TurnTransactionEngine`** — transportagnostischer Auth-/Message-Kern (401/438-State-Machine), geteilt von beiden Transporten.
- **`TurnClientTransport`** — Ein-Socket-pro-Transaktion, UDP-RTO + Stream.
- **`TurnControlTransactor`** — geteilter Socket, inbound via `OnControlDatagram`, RTO-Backoff; propagiert Cancellation als Exception (bewusst anders als ICE-Registry).
- **`TurnResponseValidator`** — geteilte Challenge/Error-Semantik (sicherheitskritisch, darf nicht divergieren).
- **`TurnRelayControlClient`** / **`TurnRelayAllocator`** / **`TurnRelayCoordinator`** — Allocate→CreatePermission→ChannelBind-Sequenz über geteilten Media-Socket, Credential-Threading.
- **`TurnRelayChannel`** (ChannelData-Fast-Path, `IRelayDatagramChannel`) / **`TurnRelayIndicationChannel`** (Send/Data-Indication, `IRelayIndicationChannel`) — beide mit striktem Relay-Source-Filter.
- **`TurnRelayCandidateSendPath`** — Relay-ICE-Kandidaten-Send-Path mit per-IP-dedupliziertem Permission-Cache (`Lazy<Task>`, Gate für NONCE-Threading).
- **Keepalive-Loops** (alle `IRelayKeepAlive`): `TurnAllocationRefreshLoop` (½ Lifetime, Teardown via Refresh-0), `TurnChannelRebindLoop` (½ Channel-Lifetime), `TurnPermissionRefreshLoop` (½ Permission-Lifetime).
- **`TurnAllocationProbe`** — Relay-Gathering auf bereits gebundenem Media-Socket (TURN-Analog zum srflx-Probe), mit temporärem Receive-Loop und Gathering-Timeout (5 s).
- **`TurnIceRelayAllocator`** (`IIceTurnRelayAllocator`-Adapter), **`TurnTcpDataConnection`**/**`TurnTcpDataConnectionFactory`** (RFC 6062).
- Result/Options/Exception-Typen: `TurnAllocateOptions`, `TurnAllocateResult`, `TurnRefreshResult`, `TurnConnectResult`, `TurnRelayAllocation`, `TurnTransport`(enum), `TurnException`, `TurnChallengeException`.

### TURN/Server
- **`TurnServer`** — Dispatch-Kern: UDP/TCP/TLS-Loops, Slot-basierte Concurrency-Caps, Methoden-Dispatch, Relay-Receive-Pump (Data-Indication/ChannelData), Auth-Gate.
- **`TurnAllocationRegistry`** — Allokationstabelle, Mutation-Gate, Kapazitäts-Check, Hintergrund-Sweep.
- **`TurnServerAllocation`** — mutable Allokation; Permissions (IP-keyed, RFC §8), Channel-Bindings (IP:Port-keyed, §11), Quota-Gate.
- Handler: **`TurnAllocateRequestHandler`**, **`TurnRefreshRequestHandler`**, **`TurnPermissionRequestHandler`**, **`TurnTcpExtensionHandler`** (RFC 6062 Connect/ConnectionBind), **`TurnTcpPassiveConnectionService`** (ConnectionAttempt).
- Auth/Response: **`TurnServerRequestAuthenticator`**, **`TurnServerResponseFactory`**, **`TurnAuthOptions`**.
- Mobility: **`TurnMobilityService`**, **`TurnMobilityTicketStore`**, **`TurnMobilityTicketEntry`**.
- Reservierung: **`TurnPortReservation`** (record), **`TurnPortReservationStore`** (Timer-Sweep).
- TCP-Broker: **`TurnTcpConnectionBroker`**, **`TurnTcpPendingConnection`**, **`TurnStreamConnection`** (+Extension `ClientKey()`).
- **`TurnClientContext`** (record struct, `ClientKey`), **`TurnServerPermission`**, **`TurnServerChannelBinding`**, **`TurnServerTransport`**/**`TurnConnectionCapPolicy`**(enums), **`TurnServerOptions`**.

### Application/Media/Ice (reine Logik)
`IceRole`(enum), `IceCandidatePair`(+`ComputePriority`), `IceCandidatePairState`(enum), `IceCheckList` (Pairing/Pruning/Freezing), `IceConnectivityScheduler` (Waiting→InProgress→Succeeded/Failed + Unfreeze), `IceConsentFreshnessPolicy` (30 s Expiry, 0.8–1.2 Ta-Pacing), `IceConsentMonitor` (Loop mit degraded/recovered-Events), `IceInboundCheckEvaluator`(+Result), `IceRoleConflict`(+Resolution), `IceRestartDetector`, `IceTieBreaker` (`Generate`/`Derive` via SHA-256).

### Sdk (public API)
`IceConfiguration` (immutable), `IceOptions` (mutable, `ToConfiguration`), `IceServerConfiguration`, `IceServerType`(enum), `IceTransport`(enum).

### Client/Hosting
`IStunServerHost`/`StunServerHost`, `ITurnServerHost`/`TurnServerHost`, Configuration/Options-Paare für beide, `TurnServerCredential`, DI-Extensions + Fluent-Builder (`CalloraStunServerBuilder`, `CalloraTurnServerBuilder`).

## 4. Zentrale Abläufe

**STUN-Binding (Client).** `CreateBindingRequest` (Crypto-Random 12-Byte-TxID) → Encode (optional +MI) → UDP-Send mit RTO-Schedule (500 ms, ×2 bis 16 s, 7 Versuche) bzw. ein TCP/TLS-Roundtrip → Response-Matching per TxID (nicht passende Pakete verworfen) → `ProcessResponse`: 300-Redirect (einmalig, ohne Credentials/Attribute weitergereicht), 401/438 als `StunChallengeException`, sonst XOR-MAPPED bevorzugt vor MAPPED.

**STUN Long-Term-Auth.** Dreistufig: (1) unauthentifizierter Request → 401 mit REALM/NONCE, (2) Retry mit USERNAME/REALM/NONCE/MI, (3) bei 438 Stale Nonce Retry mit frischem Nonce. Identisch gespiegelt in `TurnTransactionEngine`.

**TURN-Allocation-Lebenszyklus.** `AllocateAsync` (REQUESTED-TRANSPORT, optional Lifetime/EVEN-PORT/RAF/RESERVATION-TOKEN/MOBILITY-TICKET) → Server clampt Lifetime (Default 600 s / Max 3600 s), bindet Relay-Socket (UDP-Even-Port-Paar oder TCP-Listener), prüft server-weite Quota (486), gibt XOR-RELAYED-ADDRESS + XOR-MAPPED + LIFETIME zurück. **Permission** (CreatePermission, IP-keyed, 300 s) → **Channel-Bind** (IP:Port-keyed, 600 s) → Datenpfad. **Refresh-Loop** re-issued bei ½-Lifetime, Teardown via Refresh-0 bei Dispose. Permission- und Channel-Rebind-Loops laufen analog. Effektive Credentials (Server-REALM/NONCE) werden durchgereicht, sodass nur der Allocate den Unauth-Probe-Roundtrip zahlt.

**ICE Gathering → nominiertes Paar.** srflx-Discovery (`StunIceProbe`) und Relay-Gathering (`TurnAllocationProbe`) laufen auf dem *Media-Socket*. Nach Negotiation baut `IceMediaAttachment` einen `IceNominationDriver` (nur Controlling): dieser bildet Local×Remote-Paare, ordnet nach Pair-Priority (§6.1.2.3), sendet pro Runde für das höchstpriorisierte offene Paar einen gewöhnlichen Check und — bei Erfolg — einen USE-CANDIDATE-Check; **erst nach bestätigter USE-CANDIDATE-Response** wird nominiert (nicht auf verlorenem Nominierungs-Check, nicht auf Rohpriorität). Der Controlled-Agent adoptiert das Paar über den eingehenden USE-CANDIDATE (`IceInboundStunHandler.PairNominated`). Relay-Kandidaten (Type-Preference 0) rangieren unter Host/srflx → Direktpfad-Präferenz fällt „gratis" ab. Höherpriorisierte, später funktionierende Paare re-nominieren.

**Keepalives / Consent Freshness.** `IceMediaConsentSession` sendet über `IceConsentMonitor` alle ~5 s (×0.8–1.2) einen Consent-Check zum nominierten Remote; bleibt 30 s unbeantwortet → `onConsentLost`. Zwischenzustände lösen degraded/recovered-Events aus (WebRTC-„disconnected"). Bei Relay-Nomination wird Consent auf den Relay-Send-Path umgeleitet.

## 5. Threading-, Speicher- & Fehlerbehandlungsmodell

- **Nebenläufigkeit:** Server nutzen `ConcurrentDictionary` + `SemaphoreSlim`-Mutation-Gates (Registry, Allocation-Quota); Slot-Semaphore begrenzen gleichzeitige Stream-Connections/UDP-Handler. ICE-Rollen werden lock-frei via `Volatile.Read/Write` gepflegt, der nominierte Ziel-Snapshot via einzelner atomarer Referenz-Swap (`IceNominatedTarget`), um Torn-Reads zu vermeiden. Transaction-Registries nutzen `TaskCompletionSource(RunContinuationsAsynchronously)` mit synchroner Registrierung *vor* dem Send (kein Race mit schneller Antwort).
- **Speicher:** Nonce-/Ticket-/Reservierungs-Stores mit Lazy-Purge (CAS auf `_nextPurgeAtUtcTicks`) bzw. Timer-Sweep; `ArrayPool` in Receive-Loops und TCP-Pipe; Attribut-Flood-Cap (64) und Frame-Größen-Caps als DoS-Schutz. HMAC-Schlüssel werden nach Gebrauch per `CryptographicOperations.ZeroMemory` genullt; Integritätsvergleich per `FixedTimeEquals`.
- **Fehlerbehandlung:** Konsequente „decode gibt null zurück statt Exception"-Vertrag. Server verwerfen Nicht-STUN/malformed/Auth-Fehler still (Amplification-Schutz), loggen ansonsten. Client-Transporte kapseln transportbezogene Ausnahmen in `StunException`/`TurnException`; Cancellation wird sauber von Timeout getrennt. Keepalive-Loops überleben transiente Fehler durch Backoff-Retry; Dispose ist überall idempotent und cancellation-getrieben.

## 6. Qualitätsbefunde

### Stärken
- Vorbildliche Schichtentrennung und Modul-Isolation (TURN kennt STUN nur als Wire-Codec; ICE-Entscheidungslogik ist rein/testbar).
- Durchweg RFC-genaue, exzellent kommentierte Implementierung mit Paragraphen-Verweisen; Auth-Semantik (401/438) bewusst in *einem* geteilten Validator (`TurnResponseValidator`) zentralisiert, damit sie nicht divergiert.
- Solide Sicherheits-Hygiene: konstantzeitiger HMAC-Vergleich, Schlüssel-Nullung, Crypto-RNG für TxIDs/Nonces/Tickets, Flood-/Größen-Caps, Quota-Enforcement *vor* Ressourcen-Allokation (`TurnAllocateRequestHandler.cs:109`), Relay-Source-Filter gegen ChannelData-Injection (`TurnRelayChannel.TryUnwrap`), stilles Verwerfen fehlgeschlagener ICE-Checks gegen Amplification (`IceInboundCheckProcessor.cs:328`).
- Nominierung strikt auf *bestätigte* USE-CANDIDATE-Response gated (`IceNominationDriver.cs:1463`) — korrekt und subtil richtig gegen „lokal umgeschaltet, Peer weiß nichts".

### Potenzielle Bugs / Risiken / RFC-Abweichungen

- **RFC-Abweichung — TURN-Server verlangt MESSAGE-INTEGRITY auf Send-Indications.** `TurnServer.HandleTurnIndicationAsync` (`Turn/Server/TurnServer.cs:1751`) ruft `IsAuthenticatedIndication`, das bei `RequireAuthentication=true` USERNAME/REALM/NONCE/MI auf der Send-Indication erzwingt (`TurnServerRequestAuthenticator.cs:2520–2551`). Nach RFC 5766/8656 §10 sind Send/Data-Indications **nie** authentifiziert (tragen keine MESSAGE-INTEGRITY). Der eigene `TurnClient.SendIndicationAsync` signiert zwar entsprechend (intern konsistent), aber **RFC-konforme Fremd-Clients werden abgelehnt** → Interop-Bruch. Der ChannelData-Pfad ist korrekt (unauthentifiziert, nur Permission-Check).

- **Deployment-Falle — Relay-Adresse fällt auf Loopback zurück.** `TurnAllocateRequestHandler.ResolveAdvertisedRelayedEndPoint` (`Turn/Server/TurnAllocateRequestHandler.cs:234–249`) ersetzt eine an `0.0.0.0`/`::` gebundene Relay-Adresse durch `127.0.0.1`/`::1`, wenn `LocalEndPointAdvertisementResolver` keine bessere findet. In einer realen Multi-Host-Umgebung würde damit eine **unerreichbare Loopback-Relay-Adresse** an den Client annonciert (Peer kann sie nicht kontaktieren). Es fehlt eine konfigurierbare öffentliche Relay-Adresse in `TurnServerOptions`/`TurnServerHostConfiguration` → für echte Deployments limitierend.

- **RFC-7635-Timestamp-Dekodierung mit falschem Fraktionsdivisor.** `Rfc7635AccessTokenValidator.DecodeRfc7635Timestamp` (`Stun/Server/Rfc7635AccessTokenValidator.cs:532–538`) teilt den 16-Bit-Fraktionsanteil durch `64000UL` statt durch `65536`. Da nur die Sekunden (`raw >> 16`) in die Lifetime-Prüfung eingehen und der Fraktionsanteil praktisch vernachlässigbar ist, folgenlos — aber technisch inkorrekt (Sub-Sekunden-Abweichung).

- **DNS-SRV nutzt nicht-kryptographische Transaction-ID.** `DnsSrvQuery.QueryAsync` (`Stun/Client/DnsSrvQuery.cs:263`) erzeugt die DNS-Query-ID mit `Random.Shared` statt Crypto-RNG. Die Antwort wird nur per TxID + Source-Connect gefiltert → theoretisches DNS-Spoofing-Fenster (praxisüblich, aber im Kontrast zur sonst durchgängigen Crypto-RNG-Nutzung erwähnenswert).

- **Rollenkonflikt-Auflösung nutzt `>=` statt striktem `>`.** `IceRoleConflict.Resolve` (`Application/Media/Ice/IceRoleConflict.cs:659`): `ownWins = ownTieBreaker >= peerTieBreaker`. Bei exakt gleichem Tie-Breaker gewinnen *beide* Seiten lokal → beide könnten „controlling" behalten. RFC 8445 §7.3.1.1 spezifiziert striktes „größer". Da Tie-Breaker per `IceTieBreaker.Derive(localPassword)` aus dem *pro-Session zufälligen* Passwort abgeleitet werden, ist eine Kollision extrem unwahrscheinlich, aber die Randbedingung weicht formal ab.

- **Consent-Check sendet Rollen-Attribut — potenzieller Rollenkonflikt-Trigger.** `IceMediaConsentSession` baut Consent-Checks mit dem eigenen `_controlling`-Flag und Tie-Breaker (`IceConsentCheckBuilder`). Läuft Consent parallel zum inbound-Handler und wechselt eine Seite nach Nomination die Rolle nicht konsistent, könnte ein Consent-Check einen 487-Rollenkonflikt beim Peer auslösen. In der Praxis durch die deterministische `Derive`-Ableitung (beide Richtungen desselben Agents identisch) entschärft, aber die Kopplung ist subtil.

- **`StunServer.TrackConnectionTask`-Mikrorace.** (`Stun/Server/StunServer.cs:1825`, ident. in `TurnServer:1967`) Die `ContinueWith`-Cleanup kann theoretisch vor dem `_connectionTasks[taskId]=task` laufen, wenn der Task synchron completet; Folge wäre ein nie entfernter Eintrag. Praktisch harmlos (Tasks sind async), aber ein latentes Leak-Risiko unter Shutdown-Last.

- **`StunMessageCodec.EncodeCore` — kein Schutz gegen Angabe von >65535 Byte Attributen.** Das Längenfeld ist `ushort`; bei extrem großen Nutzattributen (z. B. sehr großes SOFTWARE/Token) käme es zu stillem Überlauf des Längenfelds. Für den internen Gebrauch unkritisch, da Eingaben kontrolliert sind, aber keine defensive Prüfung vorhanden.

- **`InMemoryStunCredentialProvider` Fallback „any username match".** (`Stun/Server/InMemoryStunCredentialProvider.cs:167–175`) Für Short-Term fällt die Auflösung auf „irgendein Eintrag mit passendem Username" zurück — bei gemischten Short-/Long-Term-Einträgen desselben Users könnte der falsche Credential-Typ gewählt werden. Kommentiert als Kompatibilität, aber leicht fehleranfällig.

### Kleinere Beobachtungen
- Der `IceConnectivityScheduler`/`IceCheckList` (klassischer RFC-8445-Frozen/Waiting-Scheduler) und der `IceNominationDriver` (Shared-Socket-Ansatz) sind **zwei parallele ICE-Implementierungen**; der produktive Media-Pfad nutzt den Driver, der Scheduler wirkt wie ein früherer/alternativer Baustein (laut Kommentar „ICE I3"). Mögliche Redundanz/Verwirrungsquelle.
- `TurnAttributeType.AdditionalAddressFamily`/`AddressErrorCode`/`Icmp` sind als Enum-Codes definiert, aber weder kodiert noch dekodiert → RFC-6156-Dual-Allocation und ICMP-Fehler nicht implementiert (bewusste Scope-Grenze).
- SASLprep ohne Bidi-Prüfung (dokumentiert) — für VoIP-Credentials praxistauglich, aber keine vollständige RFC-4013-Konformität.

**Fazit:** Ein außergewöhnlich sauberer, RFC-naher und sicherheitsbewusster Stack mit klarer Schichtung. Die wichtigsten praktischen Handlungspunkte sind der **Interop-Bruch bei Send-Indications** (Auth-Zwang) und die **Loopback-Relay-Adressen-Rückfallfalle** für reale TURN-Deployments; die übrigen Befunde sind kleinere RFC-Abweichungen oder latente Randfälle.



---

# Teil 4 — SDP, Media-Dateicodecs, WebRTC-Glue, Security, Common (75+ Dateien)


Analysierter Stand: Arbeitskopie `/home/user/callora-voip-sdk`, Bereich `src/Core/Infrastructure/{Sdp, Media, WebRtc, Audio, Security, Common}` sowie `src/Core/Domain/Security`. Alle Dateien wurden vollständig gelesen.

---

## 1. Überblick & Verantwortung je Subsystem

### 1.1 SDP (`Infrastructure/Sdp`, 28 Dateien)
Vollständige, in sich geschlossene SDP-Schicht: unveränderliche Modelle (RFC 4566/8866 plus Erweiterungen: SDES RFC 4568, rtcp-mux RFC 5761, BUNDLE/MID RFC 5888/8843/9143, ICE RFC 8839/8840, DTLS-SRTP RFC 5763/8122/4145, msid RFC 8830, rid/simulcast RFC 8851/8852/8853, rtcp-fb RFC 4585, extmap RFC 8285, RTX RFC 4588), ein zeilenbasierter Parser, ein Serializer, sowie zwei Verhandlungsschichten: der reine RFC-3264-Negotiator (`SdpOfferAnswerNegotiator`) und die anwendungsnahe Fassade (`SdpUtilities`/`SdpNegotiator`), die das Application-Port `ISdpNegotiator` bedient und `CallMediaParameters` für die RTP-Schicht extrahiert. `SdpSecurityInspector` liefert reine SRTP-Signal-Interpretation für die Policy-Schicht (bewusst policiefrei).

### 1.2 Media (`Infrastructure/Media`, 13 Dateien)
Datei-basierte Audio-Codec-Adapter für Aufnahme/Wiedergabe (nicht die RTP-Media-Pipeline, die liegt in `Infrastructure/Rtp`): WAV-PCM16-Mono-Reader/Writer mit eigenem RIFF-Parser, MP3 in zwei Modi — Passthrough (frameweises Kopieren mit eigenem MPEG-Frame-Header-Parser) und Transcoding (über externes ffmpeg, WAV als Zwischenformat) — plus `AudioFileCodecRegistry` als Format→Codec-Registry und `AesGcmRecordingEncryptionProvider` für die verschlüsselte Ablage von Aufnahmedateien (AES-256-GCM, Container „VREC1").

### 1.3 WebRTC (`Infrastructure/WebRtc`, 7 Dateien)
Signalisierungsneutraler WebRTC-Peer nach dem W3C-`RTCPeerConnection`-Modell (RFC 8829): Offer/Answer via SDP-Negotiator, immer BUNDLE + rtcp-mux + DTLS-SRTP, Trickle-ICE (RFC 8838) mit Early-Bind des Media-Sockets, Kandidaten-Gathering (host/srflx/relay über STUN/TURN-Probes), Zustandsmaschine (`WebRtcConnectionState`), und die Fabrik (`WebRtcSessionFactory`), die aus lokaler+remoter Description die `BundledMediaSession` (in `Infrastructure/Rtp`) ableitet — bewusst ohne die SIP-`CallMediaParameters`-Maschinerie. `WebRtcRelayBinding` ist der TURN-bewusste Kompositionspunkt, der der Media-Session nur das protokollagnostische `RelayIceBinding` übergibt.

### 1.4 Audio (`Infrastructure/Audio`, 2 Dateien)
Geräte-Auflösung: `SilenceAudioDevice` als Null-Objekt-Default, `PlatformAudioDeviceFactory` lädt per Reflection optionale Plattform-Assemblies (`CalloraVoipSdk.Audio.Windows` / `.Linux`) und fällt sonst auf Stille zurück.

### 1.5 Security (`Infrastructure/Security`, 2 Dateien) und Domain/Security (2 Dateien)
- `SipDomainCertificateValidator`: RFC-5922-§7.1-Prüfung (SIP-Domain gegen SAN-Einträge, `sip:`/`sips:`-URI und dNSName inkl. Wildcard).
- `TlsConfiguration`: TLS-Optionen für den SIP-Transport (Zertifikat lazy geladen, optionaler RFC-5922-Check, Dev-Schalter `AcceptUntrustedCertificates`).
- `SrtpPolicy` (Disabled/Optional/Required) und `SrtpDecisionReasonCodes` (stabile Telemetrie-Reason-Codes) sind die Domänen-Verträge, die `SrtpPolicyEvaluator`/`SipCallChannelSrtpPolicyGuard` (Application/SIP-Schicht) konsumieren.

### 1.6 Common (`Infrastructure/Common`, 21 Dateien)
Querschnitts-Bausteine: threadsicherer `BoundedRingBuffer`, `DisposeAction`/`AsyncDisposeAction`, drei Netzwerk-Resolver (Advertise-Adresse, Host-String, Remote-DNS), SIP-Header-Scanner (`HeaderScanState`, `ProtocolCommonUtilities` inkl. Digest-Hashes MD5/SHA-256/SHA-512-256), das protokollagnostische Relay-Seam (Interfaces + Records, die die RTP-Transportschicht vom TURN-Modul entkoppeln) und ein zentraler Timer-Scheduler (`ScheduledActionScheduler`) als Ersatz für viele `Task.Delay`-Timer.

---

## 2. Architektur

### 2.1 SDP: Parsing → Modell → Verhandlung → Serialisierung
Pipeline: `string` → `SdpSessionParser.Parse` → `SdpSessionDescription` (immutable, `init`-only) → `SdpOfferAnswerNegotiator` → `SdpOfferAnswerResult` → `SdpSessionSerializer.Serialize` → CRLF-Text.

- **Parser** (`Parsing/SdpSessionParser.cs`): einpass-zeilenbasiert, split auf `\r\n|\n`, Dispatch nach Zeilentyp (`o`,`c`,`b`,`m`,`a`). Attribute vor der ersten m-Line landen session-level, danach im aktuellen `MediaBuilder` (mutable Zwischenzustand, `Parsing/MediaBuilder.cs`). Statische PTs ohne rtpmap erhalten Platzhalternamen `PT<n>` mit Clock 8000 (Z. 313–315); `Build` filtert die Codecs nach m-Line-Reihenfolge (rtpmap-Zeilen für nicht gelistete PTs werden verworfen). Nur die m-Line selbst ist „hart" (wirft `FormatException`), alles andere ist tolerant (unbekannte/kaputte Attribute werden ignoriert — bewusst robust gegen fremde SDP-Dialekte).
- **Modelle** parsen/serialisieren sich teils selbst (`TryParse`/`Serialize` auf `SdpCryptoAttribute`, `SdpExtmap`, `SdpFingerprint`, `SdpFmtpAttribute`, `SdpIceCandidate`, `SdpMsid`, `SdpRid`, `SdpRtcpFeedback`, `SdpSimulcast`) — konsequent `null` bei Malformed-Input.
- **Negotiator** (`OfferAnswer/SdpOfferAnswerNegotiator.cs`): reine Funktion über Modelle, keine I/O. Details in §4.
- **Fassade** (`SdpUtilities.cs` + `SdpNegotiator.cs`): statische Singletons von Parser/Serializer/Negotiator; übersetzt zwischen Application-Optionen (`SdpMediaNegotiationOptions`) und Infrastructure-Optionen (`SdpMediaOptions`), extrahiert `CallMediaParameters` (Codec-Map, DTMF-PT, RTCP-Ports, ICE, Video) und Hilfsextraktionen (`TryExtractAudioDtls/AudioCrypto/VideoCrypto/BundleMid`), die der SIP-Pfad (Enricher `CallMediaParametersDtlsEnricher`/`CallMediaParametersSrtpEnricher`) nutzt, um Schlüsselmaterial aus dem tatsächlich versendeten SDP zurückzugewinnen.

### 2.2 Media-Pipeline (Datei-Ebene)
`IAudioFileCodecRegistry` → `AudioFileCodecRegistry` (Wav/Mp3) → `IAudioFileCodec.CreateReader/WriterAsync` → frame-orientierte `IAudioFileReader/Writer`, die `AudioFileFrame`s (MediaFrame + Abspiel-Delay) liefern/konsumieren. MP3 entscheidet per `CodecName == "MP3-PASSTHROUGH"` zwischen Passthrough (eigener Frame-Parser, keine externen Abhängigkeiten) und ffmpeg-Transcoding (temporäre WAV im System-Temp, Encoding erst beim `DisposeAsync` des Writers). `AesGcmRecordingEncryptionProvider` verschlüsselt fertige Aufnahmedateien als Ganzes.

### 2.3 WebRTC-Glue
`WebRtcPeerConnection` orchestriert: Early-Bind des UDP-Sockets (`EnsureLocalMediaEndPoint`) → `CreateOffer`/`SetRemoteDescriptionAsync` über den gemeinsamen SDP-Negotiator (immer `Bundle=true`, `RtcpMux=true`, MSID stabil pro Peer) → `WebRtcSessionFactory.TryCreate` leitet aus beiden Descriptions die `BundledMediaSessionOptions` ab (Remote-Endpoint via `WebRtcRemoteEndPoint` = beste Kandidatenwahl, DTLS-Rolle aus beiden `a=setup`, MID/RID-Extension-Ids, Telephone-Event-PT, Simulcast-Encodings nur bei bestätigten recv-RIDs) → Übergabe des vorgebundenen Sockets an den Transport. Relay: Gathering allokiert TURN auf demselben Socket; `WebRtcRelayBinding.CreateFactory` baut den TURN-Kontrollstack (Indication-Channel, Transactor, Control-Client, Send-Path, Refresh-/Permission-/Rebind-Loops) und liefert ihn als `RelayIceBindingFactory` in die Session — die RTP-Schicht sieht nur die Common/Relay-Abstraktionen.

### 2.4 Nutzung der Common-Bausteine (Grep-Belege)
- **Timing**: `ScheduledActionScheduler` wird von `Sip/Transactions/Server/SipServerTransactionEngine.cs` genutzt (SIP-Timer statt vieler `Task.Delay`).
- **Collections**: `BoundedRingBuffer` in `Sip/Observability/InMemorySipTelemetrySink.cs` (Telemetrie-Ringpuffer).
- **Disposal**: `DisposeAction`/`AsyncDisposeAction` in `Sip/Transport/SipTransportRuntime.cs`.
- **Network**: `LocalEndPointHostResolver` direkt im `SdpOfferAnswerNegotiator` (o=/c=-Host); `LocalEndPointAdvertisementResolver`/`RemoteEndPointResolver` in SIP-Transport, Registration, Dialogs, Forked-Invite, sowie `Stun/Client/StunServerResolver.cs` und `Turn/Server/TurnAllocateRequestHandler.cs`.
- **Protocols**: `ProtocolCommonUtilities`/`HeaderScanState` in 11 SIP-Dateien (Digest-Auth, Reason-Header, Dialog-/Session-Utilities, `SipProtocol`-Wire-Parsing).
- **Relay**: Die Interfaces/Records werden von `Rtp/BundledMediaSession(.Options)`, `Rtp/BundledMediaTransport(.Options)`, dem gesamten `Turn/Client`-Modul und `WebRtc/WebRtcRelayBinding.cs`+`WebRtcSessionFactory.cs` konsumiert — genau die dokumentierte Entkopplung (RTP kennt TURN nicht).

---

## 3. Klassenkatalog

### 3.1 Sdp/Models (13)
| Typ | Datei | Beschreibung |
|---|---|---|
| `SdpSessionDescription` (class) | `Sdp/Models/SdpSessionDescription.cs` | Immutables Session-Modell: Origin/Connection-Adresse, SessionId/-Version, Session-Direction, Medienliste, BUNDLE-`Group`, session-level ICE-Credentials, DTLS-Fingerprint/Setup. |
| `SdpMediaDescription` (class) | `Sdp/Models/SdpMediaDescription.cs` | Eine m-Line mit allen Attributen: Port (0 = disabled), Profil, Codecs, Direction, ptime/maxptime, rtcp-mux/rtcp-Port, mid/msid, Bandbreite, fmtp, rtcp-fb, extmap, rid/simulcast, ICE (candidates, ufrag/pwd/options, end-of-candidates), Fingerprint/Setup, `a=crypto`, per-Media `c=`. |
| `SdpCodecDefinition` (class) | `Sdp/Models/SdpCodecDefinition.cs` | Codec-Deskriptor: PT, Name, ClockRate, Channels (Default 1). |
| `SdpCryptoAttribute` (class) | `Sdp/Models/SdpCryptoAttribute.cs` | `a=crypto` (RFC 4568): Tag, Suite, KeyParams, SessionParams; TryParse/Serialize. |
| `SdpExtmap` (record) | `Sdp/Models/SdpExtmap.cs` | `a=extmap` (RFC 8285): Id, optionale Direction, URI; Erweiterungs-Attribute nach der URI werden verworfen. |
| `SdpFingerprint` (class) | `Sdp/Models/SdpFingerprint.cs` | `a=fingerprint` (RFC 8122): Algorithmus + Hex-Wert. |
| `SdpFmtpAttribute` (class) | `Sdp/Models/SdpFmtpAttribute.cs` | `a=fmtp`: PT + roher Parameterstring. |
| `SdpIceCandidate` (class) | `Sdp/Models/SdpIceCandidate.cs` | `a=candidate` (RFC 8839): Foundation, Component, Transport, Priority, Adresse/Port, Typ, raddr/rport, generation/ufrag/network-id-Extensions. |
| `SdpMediaDirection` (enum) | `Sdp/Models/SdpMediaDirection.cs` | SendRecv/SendOnly/RecvOnly/Inactive. |
| `SdpMsid` (record) | `Sdp/Models/SdpMsid.cs` | `a=msid` (RFC 8830): StreamId + optionale TrackId. |
| `SdpRid` (record) | `Sdp/Models/SdpRid.cs` | `a=rid` (RFC 8851): Id, Direction (nur send/recv), Restriktionen verbatim. |
| `SdpRtcpFeedback` (record) | `Sdp/Models/SdpRtcpFeedback.cs` | `a=rtcp-fb` (RFC 4585): PT (Zahl oder `*`), Feedback-Typ, Parameter. |
| `SdpSimulcast` (record) | `Sdp/Models/SdpSimulcast.cs` | `a=simulcast` (RFC 8853): Send-/Recv-Listen; Komma-Alternativen verbatim. |

### 3.2 Sdp/Parsing (5)
| Typ | Datei | Beschreibung |
|---|---|---|
| `ISdpSessionParser` | `Sdp/Parsing/ISdpSessionParser.cs` | Vertrag: SDP-Text → Modell, wirft bei kaputten Pflichtzeilen. |
| `ISdpSessionSerializer` | `Sdp/Parsing/ISdpSessionSerializer.cs` | Vertrag: Modell → CRLF-Text. |
| `SdpSessionParser` | `Sdp/Parsing/SdpSessionParser.cs` | Der zeilenbasierte Parser (siehe §2.1); private Helfer für m-Line, rtpmap, Adressen, Bandbreite, Direction-Token. |
| `MediaBuilder` | `Sdp/Parsing/MediaBuilder.cs` | Mutabler Sammelzustand pro m-Line während des Parse-Passes; `Build` erzeugt die immutable `SdpMediaDescription` in m-Line-PT-Reihenfolge. |
| `SdpSessionSerializer` | `Sdp/Parsing/SdpSessionSerializer.cs` | Serialisiert Session-Header (`v/o/s/c/t`), session-level ICE/DTLS/Gruppe/Direction, dann pro Media: m-Line, c-Override, b, mid, msid, Fingerprint/Setup, crypto, rtpmap, fmtp, rtcp-fb, extmap, rid/simulcast, Direction, ptime, rtcp-mux/rtcp, ICE, Kandidaten, end-of-candidates; erzwingt CRLF. |

### 3.3 Sdp/OfferAnswer (5 Dateien, 8 Typen)
| Typ | Datei | Beschreibung |
|---|---|---|
| `ISdpOfferAnswerNegotiator` | `Sdp/OfferAnswer/ISdpOfferAnswerNegotiator.cs` | Vertrag: `CreateOffer` + `NegotiateAnswer`. |
| `SdpOfferAnswerNegotiator` | `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs` | RFC-3264-Kernlogik: Codec-Schnittmenge, Direction-Auflösung, fmtp-Carry, ptime-Spiegelung, telephone-event, rtcp-mux, BUNDLE/MID, SDES vs. DTLS (fail-closed), Video-Answer inkl. RTX/Feedback/extmap/rid, Zero-Port-Mirror für abgelehnte m-Lines. |
| `SdesCryptoSelector` (static) + `Selection` (record) | `Sdp/OfferAnswer/SdesCryptoSelector.cs` | Reine SDES-Auswahl: erste unterstützte Suite mit `inline:`-Key, Answer mit *eigenem* frischen Key (RFC 4568 §5.1.3), Offer-Builder (Default-Suite AES_CM_128_HMAC_SHA1_80, Tag 1, Re-Offer mit Bestandskey). |
| `SdpDtlsParameters` | `Sdp/OfferAnswer/SdpMediaOptions.cs` | DTLS-Identität für SDP: Algorithmus, Fingerprint, Setup (Default `actpass`). |
| `SdpIceParameters` | ebd. | ICE-Ufrag/Pwd/Options + Kandidatenliste. |
| `SdpVideoMediaOptions` | ebd. | Video-Port, Codecs, per-m-Line-Crypto/Kandidaten, HeaderExtension-URIs, Simulcast-Send-RIDs. |
| `SdpMediaOptions` | ebd. | Gesamtoptionen: Dtls, Video, Crypto (nur Offer), Audio/VideoMsid, Ice, RtcpMux, Bundle, SessionId/-Version. |
| `SdpOfferAnswerResult` | `Sdp/OfferAnswer/SdpOfferAnswerResult.cs` | Ergebnis: Success, Answer-Modell, NegotiatedCodecs, RtcpMuxNegotiated, RemoteFingerprint/-Setup, NegotiatedCrypto (Remote-Key, inbound), LocalCrypto (eigener Key, outbound). |

### 3.4 Sdp (Wurzel, 5)
| Typ | Datei | Beschreibung |
|---|---|---|
| `SdpNegotiator` | `Sdp/SdpNegotiator.cs` | `ISdpNegotiator`-Adapter (Application-Port) → delegiert an `SdpUtilities`, injiziert Logger für Observability unparsebarer Remote-SDP (HARD-G3). |
| `SdpBundleMidInfo` (record) | `Sdp/SdpBundleMidInfo.cs` | Aus SDP rekonstruierte BUNDLE-Fakten: gemeinsame MID-Extension-Id + Audio-/Video-MID. |
| `SdpSecurityInspector` (static) | `Sdp/SdpSecurityInspector.cs` | SRTP-Signal-Erkennung (`SAVP`-Profil, crypto, Fingerprint, setup), `IsSecureProfile`, `IsDtlsProfile` (`UDP/TLS/…`); reine Interpretation, keine Policy. |
| `SdpUtilities` (static) | `Sdp/SdpUtilities.cs` | Fassade: Default-Codecs (G722, PCMA, PCMU, telephone-event 101; Opt-in Opus 107/48000/2), Offer-/Answer-Builder, `TryParseMediaParameters` (inkl. Video, ICE, RTCP-Ports, DTMF-PT-Auswahl nach Clock), Hold-Erkennung, Crypto-/DTLS-/BundleMid-Extraktion, Codec-Ranking/Präferenzen, Options-Konvertierung. |
| `VideoCodecCatalog` (static) | `Sdp/VideoCodecCatalog.cs` | Video-Fähigkeiten (VP8 PT96, H264 PT97, je 90 kHz), packetization-mode=1-Erfordernis, Standard-RTCP-Feedback (nack, nack pli, ccm fir), Feedback-Schnittmenge (Answer immer `*`, dokumentierte DECISION), RTX-Build/-Negotiate/-Lookup (`apt`-fmtp). |

### 3.5 Media (13)
| Typ | Datei | Beschreibung |
|---|---|---|
| `AesGcmRecordingEncryptionProvider` | `Media/AesGcmRecordingEncryptionProvider.cs` | AES-256-GCM-Dateiverschlüsselung (`VREC1`+Nonce(12)+Tag(16)+Ciphertext), Konstruktor mit Raw-Key oder PBKDF2-SHA256-Ableitung (≥10k Iterationen, Salt ≥8 B). |
| `AudioFileCodecRegistry` | `Media/AudioFileCodecRegistry.cs` | Registry `AudioFileFormat` → `IAudioFileCodec` (Wav, Mp3). |
| `FfmpegProcessRunner` (static) | `Media/FfmpegProcessRunner.cs` | Startet ffmpeg-Prozesse, strukturierte Fehler (stderr in Exception), `IsAvailable()`-Probe. |
| `Mp3AudioFileCodec` | `Media/Mp3AudioFileCodec.cs` | Dispatcher: `MP3-PASSTHROUGH` → Passthrough-Reader/Writer, sonst Transcoding via ffmpeg; Best-effort-`TryDeleteFile`. |
| `Mp3FrameHeader` (readonly record struct) | `Media/Mp3FrameHeader.cs` | FrameLength, SampleRate, SamplesPerFrame. |
| `Mp3FrameParser` (static) | `Media/Mp3FrameParser.cs` | MPEG-Audio-Layer-III-Header-Parser (MPEG1/2/2.5, Bitraten-/Samplerate-Tabellen, Padding, Framelänge). |
| `Mp3PassthroughReader` | `Media/Mp3PassthroughReader.cs` | Liest Frame für Frame (4-Byte-Header → Framelänge), berechnet Abspiel-Delay und RTP-Dauer über den Ziel-Clock (Default 90 kHz). |
| `Mp3PassthroughWriter` | `Media/Mp3PassthroughWriter.cs` | Schreibt MPEG-Frames durch; validiert nur den ersten Frame-Header. |
| `Mp3TranscodingReader` | `Media/Mp3TranscodingReader.cs` | ffmpeg MP3→temp-WAV (mono), delegiert an `WavAudioFileReader`; löscht Temp-Datei bei Dispose/Fehler. |
| `Mp3TranscodingWriter` | `Media/Mp3TranscodingWriter.cs` | Schreibt PCM16 in temp-WAV; beim `DisposeAsync` ffmpeg-Encode nach MP3 (libmp3lame, q=4). |
| `WavAudioFileCodec` | `Media/WavAudioFileCodec.cs` | WAV-PCM16-Mono-Fabrik + 44-Byte-Header-Writer (Konstanten). |
| `WavAudioFileReader` | `Media/WavAudioFileReader.cs` | RIFF-Chunk-Parser (fmt-Validierung: PCM/mono/16 bit, data-Chunk, odd-Padding), frameweises Lesen (`SamplesPerFrame*2` Bytes), Delay aus SampleRate. |
| `WavAudioFileWriter` | `Media/WavAudioFileWriter.cs` | Schreibt Platzhalter-Header, zählt `_dataLength`, patcht den Header beim Dispose. |

### 3.6 WebRtc (7)
| Typ | Datei | Beschreibung |
|---|---|---|
| `WebRtcPeerConnection` | `WebRtc/WebRtcPeerConnection.cs` | Der Peer (905 Zeilen): Offer/Answer, Early-Bind-Socket, Trickle-ICE (Kandidaten-Puffer + Live-Feed), Gathering (STUN srflx, TURN relay inkl. Allocation-Retention/Adoption), Zustandsmaschine, Events (State, Audio, VideoFrame, DTMF, LocalIceCandidate), Send-APIs mit Drain-Lease, Dispose-Ordnung. |
| `SendDrainGate` | `WebRtc/SendDrainGate.cs` | Drain-Gate (TryEnter/Exit/BeginDrainAsync) gegen die Send-vs-Dispose-Race (HARD-C6). |
| `WebRtcConnectionState` (enum) | `WebRtc/WebRtcConnectionState.cs` | New/Connecting/Connected/Disconnected/Failed/Closed nach W3C `RTCPeerConnectionState`. |
| `WebRtcPeerOptions` (record) | `WebRtc/WebRtcPeerOptions.cs` | Lokale Konfiguration: Endpoint, Audio-/Video-Codecs, DTLS-Identität, ICE-Credentials, IceServers. |
| `WebRtcRelayBinding` (static) | `WebRtc/WebRtcRelayBinding.cs` | Baut den TURN-Kontrollstack als `RelayIceBindingFactory` (Indication, Transactor, Control-Client, Send-Path, Allocation-Refresh + Permission-Refresh als `CompositeRelayKeepAlive`, ChannelBind 0x4000 + Rebind-Loop). |
| `WebRtcRemoteEndPoint` (static) | `WebRtc/WebRtcRemoteEndPoint.cs` | Remote-Media-Endpoint-Auswahl: höchstpriorer component-1-UDP-Kandidat, sonst m-Line-Adresse/Port; ausdrücklich kein volles ICE. |
| `WebRtcSessionFactory` (static) | `WebRtc/WebRtcSessionFactory.cs` | Description-Paar → `BundledMediaSession`: MID/RID-Ids, DTLS-Rolle, Remote-Kandidatenliste, Audio-Track (20-ms-Frames, DTMF-PT mit Kollisionsabwehr), Video-Track (Direction-Gates, Simulcast nur bei bestätigten recv-RIDs), zufällige, kollisionsfreie SSRCs. |

### 3.7 Audio (2)
| Typ | Datei | Beschreibung |
|---|---|---|
| `PlatformAudioDeviceFactory` (static) | `Audio/PlatformAudioDeviceFactory.cs` | Auto-Selektion des Plattformgeräts per Reflection (Windows/Linux-Assemblies), sonst Silence; `ownsResolvedDevice`-Flag für die Lebenszyklus-Verantwortung. |
| `SilenceAudioDevice` | `Audio/SilenceAudioDevice.cs` | Null-Objekt-`IAudioDevice`+`IAudioDeviceRuntimeControl`: sendet Stille, verwirft Empfangenes; Singleton `Instance`. |

### 3.8 Security + Domain/Security (4)
| Typ | Datei | Beschreibung |
|---|---|---|
| `SipDomainCertificateValidator` (static, public) | `Security/SipDomainCertificateValidator.cs` | RFC-5922-SAN-Prüfung: URI-SAN (sip:/sips:, Host-Extraktion mit user/port/param-Stripping) und DNS-SAN (exakt + `*.`-Wildcard nur für das linkeste Label); plattformtolerantes SAN-Parsing über `X509Extension.Format`. |
| `TlsConfiguration` (public) | `Security/TlsConfiguration.cs` | Zertifikatspfad/-passwort (lazy geladen, SYSLIB0057 unterdrückt), `AcceptUntrustedCertificates`, `ExpectedSipDomain` + `ValidatePeerCertificateSipDomain` (übersprungen, wenn nicht gesetzt). |
| `SrtpDecisionReasonCodes` (static, public) | `Domain/Security/SrtpDecisionReasonCodes.cs` | Stabile Reason-Codes (`srtp.negotiated`, `srtp.required.remote_no_srtp`, `srtp.disabled.rejected_remote_srtp`, …). |
| `SrtpPolicy` (enum, public) | `Domain/Security/SrtpPolicy.cs` | Disabled/Optional/Required. |

### 3.9 Common (21 Dateien)
| Typ | Datei | Beschreibung |
|---|---|---|
| `BoundedRingBuffer<T>` | `Common/Collections/BoundedRingBuffer.cs` | Lock-basierter Ringpuffer, überschreibt Ältestes, `Snapshot()` in stabiler Reihenfolge. |
| `AsyncDisposeAction` / `DisposeAction` | `Common/Disposal/*.cs` | Einmal-Ausführung einer Cleanup-Action (Interlocked-Guard), async/sync. |
| `LocalEndPointAdvertisementResolver` (static) | `Common/Network/LocalEndPointAdvertisementResolver.cs` | Ersetzt Wildcard-/Loopback-Bindeadresse per UDP-Connect-Probe durch die geroutete Interface-Adresse, behält den gebundenen Port; best-effort. |
| `LocalEndPointHostResolver` (static) | `Common/Network/LocalEndPointHostResolver.cs` | Wildcard→Loopback-Hoststring für SDP/SIP-Bodies. |
| `RemoteEndPointResolver` (static) | `Common/Network/RemoteEndPointResolver.cs` | Host/Port→`IPEndPoint`, DNS mit IPv4-Präferenz. |
| `HeaderScanState` (mutable struct) | `Common/Protocols/HeaderScanState.cs` | Einpass-Scanner für RFC-3261-Headerwerte: quoted-string inkl. `\`-Escape, `<…>`-Tiefe; liefert „strukturell?" pro Zeichen. |
| `ProtocolCommonUtilities` (static) | `Common/Protocols/ProtocolCommonUtilities.cs` | Komma-Split unter Quote-/Bracket-Respekt, Token-Suche, MD5-Hex, `TryHashHexLower` (MD5/SHA-256/SHA-512-256 via BouncyCastle `Sha512tDigest` — RFC 8760), Quoted-Value-Escaping. |
| `CompositeRelayKeepAlive` | `Common/Relay/CompositeRelayKeepAlive.cs` | Bündelt Keepalive-Loops; Start in Reihenfolge, Dispose in Gegenrichtung, aggregiert Fehler (`AggregateException`). |
| `IRelayControlTransport` | `Common/Relay/IRelayControlTransport.cs` | Koordinator-Seam auf dem Transport: Control-Send + Kanal-Installation. |
| `IRelayDatagramChannel` | `Common/Relay/IRelayDatagramChannel.cs` | An einen Peer gebundener Relay-Datenkanal (Wrap/TryUnwrap/IsFromRelay) — TURN-ChannelData-Abstraktion. |
| `IRelayIndicationChannel` | `Common/Relay/IRelayIndicationChannel.cs` | Per-Datagram-adressierender Kanal (Send/Data-Indications) für die ICE-Check-Phase. |
| `IRelayKeepAlive` | `Common/Relay/IRelayKeepAlive.cs` | Start-idempotenter Keepalive mit Teardown im Dispose. |
| `RelayChannelBinding` (record) | `Common/Relay/RelayChannelBinding.cs` | Ergebnis des ChannelBind: Kanal + optionaler Rebind-Keepalive. |
| `RelayEndPoint` (static) | `Common/Relay/RelayEndPoint.cs` | Endpoint-Gleichheit mit IPv4-mapped-IPv6-Kanonisierung (Dual-Stack-robust). |
| `RelayIceBinding` (record) | `Common/Relay/RelayIceBinding.cs` | Die drei Seams eines Relay-Kandidaten: Indication-Channel, Control-Sink, RelaySend + KeepAlive + BindChannel-Delegate. |
| `RelayIceBindingFactory` (delegate) | `Common/Relay/RelayIceBindingFactory.cs` | Deferred-Konstruktion des Bindings, sobald der geteilte Socket existiert. |
| `IScheduledActionScheduler` | `Common/Timing/IScheduledActionScheduler.cs` | One-shot-Delay-Scheduler-Vertrag. |
| `ScheduledActionEntry` | `Common/Timing/ScheduledActionEntry.cs` | Queue-Eintrag (Id, DueAtTicks, Callback, IsCanceled-Tombstone). |
| `ScheduledActionHandle` | `Common/Timing/ScheduledActionHandle.cs` | Idempotentes Cancel-Handle. |
| `ScheduledActionScheduler` | `Common/Timing/ScheduledActionScheduler.cs` | Ein Worker-Loop + `PriorityQueue` + `SemaphoreSlim`-Signal; Tombstone-Cancellation, Callback-Fehler werden geloggt, Dispose wartet ≤2 s. |

---

## 4. Zentrale Abläufe

### 4.1 Offer-Erzeugung (`SdpOfferAnswerNegotiator.CreateOffer`)
Profilwahl: DTLS → `UDP/TLS/RTP/SAVPF`; sonst SDES-Crypto vorhanden → `RTP/SAVP`; sonst `RTP/AVP` (Z. 35–39). Audio-m-Line mit Codecs in Präferenzordnung, telephone-event-fmtp `0-16` automatisch (`BuildFmtpForCodecs`). Bei `Bundle=true`: `a=group:BUNDLE audio [video]`, `a=mid`, und die MID-SDES-Extension wird auf jeder m-Line **zuerst** angeboten, damit `BuildOfferExtmaps` ihr auf allen m-Lines dieselbe Id 1 zuweist (RFC 8843 §9). Video optional mit RTX-Repair-PTs (`apt`-fmtp, PTs oberhalb des höchsten Video-PT), Standard-RTCP-Feedback und Simulcast (`a=rid … send pt=<primär>` + `a=simulcast:send`, RID-Extension vor App-Extensions).

### 4.2 Answer-Verhandlung (`NegotiateAnswer`)
1. Erste Audio-m-Line suchen; fehlt sie → `Success=false`; Port 0 → gespiegelte Disabled-Answer.
2. **Codec-Auswahl** (`NegotiateCodecs`, Z. 356–415): Identitätsmatch Name(uppercase):Clock[/Channels]; statische PTs ohne rtpmap werden über `ResolveEffectiveName` (PT0→PCMU, PT8→PCMA, PT9→G722) auf benannte Fähigkeiten abgebildet (Fritz!Box-Interop, 488-Vermeidung). telephone-event wird bei beliebigem angebotenem PT übernommen. Fallback: reine Static-PT-Schnittmenge, wenn kein Namensmatch. Antwort behält die **Offer-PT-Nummerierung** (RFC 3264 §6.1). Enthält die Auswahl nur telephone-event → Fehlschlag (kein audiolos verhandelter Call).
3. **Direction** (`ResolveAnswerDirection`): Inaktiv dominiert; sendonly↔recvonly-Spiegelung; sendonly+sendonly → inactive.
4. **rtcp-mux**: gespiegelt, wenn angeboten *oder* lokal gewünscht (siehe Befund B3).
5. **BUNDLE**: `a=group` nur übernommen, wenn Offer-MID vorhanden; nach dem m-Line-Aufbau wird die Gruppe auf akzeptierte MIDs reduziert (RFC 9143 §7.3.3, Z. 319–326).
6. **Keying, fail-closed**: SDES nur auf Nicht-DTLS-Profilen (Guard HARD-S1, Z. 220); Answer trägt eigenen frischen Key bei gleicher Suite/Tag (`SdesCryptoSelector`). DTLS-Antwort nur, wenn der Peer einen Fingerprint offeriert hat und SDES nicht gewann; `ResolveAnswerSetup`: actpass→active, active→passive, passive→active, sonst passive. Ein SAVP(F)-Profil ohne verhandelbaren Schlüssel und ein `UDP/TLS/*`-Profil ohne DTLS-Antwort werden abgelehnt — kein Downgrade auf Klartext-RTP (Z. 256–264).
7. **fmtp/ptime**: fmtp der akzeptierten PTs aus dem Offer übernommen; ptime reflektiert.
8. **m-Line-Vollständigkeit** (RFC 3264 §6): eine Answer-m-Line pro Offer-m-Line in Reihenfolge; erste Video-m-Line wird ggf. verhandelt (`TryNegotiateVideoAnswerMedia`: Codecs nur per Name+Clock — bewusst ohne Static-PT-Fallback; H264 erfordert `packetization-mode=1`; RTX-Echo für akzeptierte PTs; Feedback-Schnittmenge; extmap-Echo mit Offer-Ids), alle weiteren m-Lines Zero-Port-Mirror.
9. Ergebnis trägt zusätzlich die Media-Layer-Shortcuts (RemoteFingerprint/-Setup, Negotiated-/LocalCrypto, RtcpMuxNegotiated).

### 4.3 DTMF (telephone-event, RFC 4733)
- Offer: PT 101/8000 in den Defaults, fmtp `0-16` automatisch.
- Parametrisierung (`SdpUtilities.ResolveTelephoneEventPayloadType`, Z. 351–383): bevorzugt die event-Zeile, deren Clock dem verhandelten Audio-Codec entspricht (sipgate-Muster: 101/48000 für Opus, 113/8000 für G.711); Fallback erste event-Zeile; zuletzt Heuristik über fmtp `0-16` ohne rtpmap.
- WebRTC-Pfad (`WebRtcSessionFactory.ResolveTelephoneEvent`, Z. 192–207) spiegelt dieselbe Logik und entschärft zusätzlich eine PT-Kollision zwischen Audio-Codec und telephone-event (feindliches SDP würde sonst alle Audio-Pakete in den DTMF-Pfad lenken, Z. 108–113). `SendDtmfAsync`/`DtmfReceived` auf dem Peer sind reine Durchreichungen zur `BundledMediaSession`.

### 4.4 Media-Session-Aufbau (WebRTC)
`CreateOffer`/`SetRemoteDescriptionAsync` → (bei Answerer) `NegotiateAnswer`; danach `WebRtcSessionFactory.TryCreate`: verlangt Audio-m-Line beidseitig, lokale MID + MID-Extension-Id (sonst kein BUNDLE → keine Session, nur Log-Warnung; `StartAsync` wirft dann), Remote-Fingerprint (WebRTC ist DTLS-only). Remote-Endpoint über Kandidat oder m-Line; Remote-Kandidatenliste für die Connectivity-Checks (Fallback: der aufgelöste Endpoint mit Host-Priorität). DTLS-Client-Rolle aus beiden `a=setup` (Default Client). Audio-Track: 20-ms-Frames, verhandelter Clock, DTMF-PT/-Clock; `AudioSendEnabled` aus den Directions (Audio wird bei Nicht-Senden unterdrückt statt verworfen, da die Audio-m-Line den Bundle-Transport ankert). Video-Track nur bei beidseitiger Send/Recv-Negotiation; Simulcast nur, wenn die Remote-Answer recv-RIDs **und** die RID-Extension bestätigt (sonst Single-Stream-Fallback). Anschließend Socket-Übergabe (`_socketHandedOver`), Event-Verkabelung (`WireSession`: Connected/Failed/Disconnected-Mapping), Nachreichen gepufferter Trickle-Kandidaten, Zustand `Connecting`; `StartAsync` startet Receive-Loop, Consent-Loop und DTLS-Handshake → `Connected` bei installierten SRTP-Keys.

---

## 5. Threading-, Speicher- und Fehlerbehandlungsmodell

**Threading**
- SDP-Schicht: vollständig zustandslos/immutable nach dem Parse; die statischen Singletons in `SdpUtilities`/`SdpSecurityInspector` sind unproblematisch, da Parser/Serializer/Negotiator keinerlei Instanzzustand tragen.
- `WebRtcPeerConnection`: dokumentierter Vertrag (Kopf-Kommentar, Z. 30–41): der Signalisierungs-Handshake ist eine **Single-Caller-Sequenz**; `_sync` (Monitor) schützt die Felder, macht aber Out-of-Order-Signaling nicht sinnvoll. Der Media-Hotpath ist gegen konkurrierendes Dispose über `SendDrainGate` gehärtet (Lease pro Send, Dispose drained). Trickle-Kandidaten werden unter demselben Lock gepuffert, unter dem `_session` publiziert wird — kein Verlustfenster. Event-Handler-Ausnahmen werden gefangen und geloggt (Z. 685–691, 866–874).
- `ScheduledActionScheduler`: ein einzelner Worker-Task, `lock`-geschützte PriorityQueue, Cancel als Tombstone; Callbacks laufen auf dem Worker-Thread (lange Callbacks verzögern nachfolgende — implizite Anforderung an die Nutzer).
- `BoundedRingBuffer`, `SendDrainGate`, `DisposeAction`s: klassische Lock-/Interlocked-Muster, korrekt.
- Media-Dateiadapter: nicht threadsicher (ein Reader/Writer = ein Konsument), was dem Verwendungsmuster entspricht; `_disposed`-Flags sind einfache bools ohne Synchronisation.

**Speicher**
- SDP: kurzlebige Allokationen (Split-Arrays, LINQ) — für Signalisierungsraten unkritisch; keine Pufferwiederverwendung nötig.
- Media: WAV/MP3-Reader allozieren pro Frame ein neues Array (kein Pooling); `AesGcmRecordingEncryptionProvider` lädt die gesamte Datei in den Speicher (s. Befund B12). `WavAudioFileWriter._dataLength` ist `uint` (4-GiB-Grenze des RIFF-Formats implizit).
- WebRTC: Empfangspuffer des Media-Sockets fest 8192 B (s. Befund B7); Trickle-Puffer unbegrenzt, aber praktisch klein.
- Scheduler: stornierte Einträge bleiben bis zur Fälligkeit als Tombstones in Queue/Dictionary (bounded, aber verzögerte Freigabe).

**Fehlerbehandlung**
- Konsistentes Muster „untrusted remote input → Try*/null + Debug-Log" (HARD-G3): `SdpUtilities.TryBuildNegotiatedAnswer`/`TryParseMediaParameters`/`IsRemoteHoldSdp` und `SdpSecurityInspector.TryInspectAudioSecurity` fangen breit (bewusst: ein malformer INVITE darf den Signalisierungspfad nicht crashen), loggen aber. `IsRemoteHoldSdp` hat zusätzlich einen Substring-Fallback (`a=sendonly`/`a=inactive`).
- Fail-closed-Security: keyless SAVP, SDES auf DTLS-Profil, DTLS ohne Fingerprint → Ablehnung statt Klartext (Negotiator Z. 256–264, 519–523; `SdpUtilities.TryResolveVideoParameters` Z. 539–562 spiegelt das für die Parameter-Extraktion).
- Media: Validierungsfehler als `InvalidOperationException` mit präziser Botschaft; ffmpeg-Fehler mit stderr-Auszug; Temp-Datei-Cleanup best-effort.
- WebRTC: `SetRemoteDescriptionAsync` unterscheidet `ArgumentException` (kein SDP) und `InvalidOperationException` (nicht verhandelbar, Zustand → Failed); Dispose-Ordnung Drain → Session → Orphan-Socket, idempotent.

---

## 6. Qualitätsbefunde

### Stärken
- **RFC-Disziplin und Dokumentation**: nahezu jede Entscheidung ist mit RFC-Abschnitt und Begründung kommentiert, inklusive markierter bewusster Abweichungen („DECISION", „Limitation", „follow-up") — z. B. `VideoCodecCatalog.NegotiateFeedback` (Answer immer `*`), `WebRtcSessionFactory.ConfirmedSimulcastRids` (Komma-Alternativen konservativ unbestätigt), `WebRtcRemoteEndPoint` (Single-Candidate statt Voll-ICE).
- **Sicherheits-Korrektheit**: eigener SDES-Key in der Answer (RFC 4568 §5.1.3), fail-closed statt Downgrade, DTLS/SDES-Exklusivität pro m-Line (HARD-S1-Guard), DTMF-PT-Kollisionsabwehr, Re-Offer ohne Rekeying (Hold/Unhold).
- **Interop-Härtung**: Static-PT-Auflösung (Fritz!Box), Clock-passende telephone-event-Auswahl (sipgate), mDNS-Kandidaten-Diagnose, IPv4-mapped-IPv6-Kanonisierung im Relay-Filter.
- **Saubere Entkopplung**: das Relay-Seam in `Common/Relay` hält RTP-Transport und TURN-Modul vollständig getrennt; WebRTC-Pfad umgeht die SIP-Maschinerie bewusst.
- **Nebenläufigkeit**: `SendDrainGate` + dokumentierte Dispose-/Publikationsordnung im Peer sind sorgfältig konstruiert (Kandidaten-Puffer unter dem Session-Publikations-Lock, Adoption außerhalb des Locks).

### Potenzielle Bugs / Risiken

**B1 — XML-Doku widerspricht Implementierung bei `ResolveAnswerSetup`** — `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs:743` vs. `:755`. Die Doku behauptet „holdconn oder null → actpass", der Code liefert für beide `passive` (mit korrekter RFC-5763-§5-Begründung im Inline-Kommentar). Der Code ist richtig, die XML-Doku veraltet. Zudem ist `passive` als Antwort auf ein fehlendes `a=setup` (Offer-Default `active` per RFC 4145 §4) korrekt, für `holdconn` aber fragwürdig (RFC 4145: holdconn = Verbindung noch nicht aufbauen).

**B2 — `b=TIAS` wird als kbps interpretiert und als `b=AS` re-serialisiert** — `Sdp/Parsing/SdpSessionParser.cs:369-377` parst den Zahlenwert jeder `b=`-Zeile (Kommentar nennt explizit `TIAS:64000`) in das kbps-Feld `Bandwidth`; `Sdp/Parsing/SdpSessionSerializer.cs:76-77` emittiert immer `b=AS:`. Ein TIAS-Offer (bps) würde als AS-Wert (kbps) um Faktor 1000 verfälscht weitergereicht, falls das Feld je zurückgeschrieben wird (derzeit nur beim Durchreichen relevant, Negotiator setzt Bandwidth nicht in Answers — Latenzrisiko).

**B3 — rtcp-mux in der Answer ohne Offer** — `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs:199`: `var rtcpMux = offeredAudio.RtcpMux || localOptions?.RtcpMux == true;`. RFC 5761 §5.1.1 erlaubt `a=rtcp-mux` in der Answer nur, wenn es im Offer stand. Mit lokal aktiviertem RtcpMux gegen ein Offer ohne mux entsteht eine nicht angebotene Attribut-Zusage; zudem meldet `RtcpMuxNegotiated=true`, obwohl der Peer getrennte RTCP-Ports verwendet — potenzieller RTCP-Verlust.

**B4 — Static-PT-Fallback kann Codec-Fehlzuordnung erzeugen** — `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs:397-412`: Wenn kein Namensmatch existiert, matcht der Fallback rein über PT-Nummern. Ein Offer, das einen dynamischen (oder umgewidmeten) PT verwendet, der zufällig mit einem lokalen PT kollidiert (z. B. rtpmap `foo/8000` auf PT 9), würde als G722 beantwortet, obwohl der Peer etwas anderes meinte. Das Fenster ist klein (nur bei null Namensmatches), aber vorhanden.

**B5 — fmtp-Echo statt eigener Empfangsfähigkeiten** — `Sdp/OfferAnswer/SdpOfferAnswerNegotiator.cs:189-193` und `:537`: Die Answer übernimmt die fmtp-Zeilen des Offerers verbatim. Für symmetrische Parameter unkritisch; für H.264 `profile-level-id`/asymmetrische Parameter (RFC 6184 §8.2.2) ist das eine Vereinfachung, die eigene Fähigkeiten überzeichnen kann.

**B6 — WebRTC-Media-Socket ist IPv4-only** — `WebRtc/WebRtcPeerConnection.cs:772`: `new UdpClient(AddressFamily.InterNetwork)` ignoriert die Familie von `_options.LocalEndPoint`; ein IPv6-`LocalEndPoint` führt zu einem Bind-Fehler (`SocketException`) statt zu einem IPv6-Peer.

**B7 — Empfangspuffer 8192 Bytes** — `WebRtc/WebRtcPeerConnection.cs:773`: `ReceiveBufferSize = 8192` ist für einen Video-fähigen Peer sehr klein (ein einzelner Burst von wenigen RTP-Paketen füllt ihn); Paketverlust unter Last ist wahrscheinlich. OS-Default oder konfigurierbar wäre robuster.

**B8 — `checked(rtpPort + 1)` kann werfen** — `Sdp/SdpUtilities.cs:498`: Bei RTP-Port 65535 ohne rtcp-mux wirft `checked` eine `OverflowException`; die landet zwar im breiten catch von `TryParseMediaParameters` (→ null), verwirft dann aber die gesamte Medienverhandlung statt nur RTCP zu degradieren. Außerdem wäre 65536 ohnehin kein gültiger Port — eine explizite Behandlung wäre klarer.

**B9 — `RemoteEndPointResolver.ResolveAsync` wirft bei leerem DNS-Ergebnis unspezifisch** — `Common/Network/RemoteEndPointResolver.cs:24-26`: `addresses.First()` produziert `InvalidOperationException („Sequence contains no elements")` statt einer aussagekräftigen DNS-Fehlermeldung.

**B10 — `MediaBuilder.Build` ignoriert den Fallback-Parameter** — `Sdp/Parsing/MediaBuilder.cs:48`: `fallbackConnectionAddress` wird nie verwendet (`ConnectionAddress = ConnectionAddress` bleibt media-level oder null); der Fallback wird stattdessen erst in `SdpUtilities.TryParseMediaParameters:232-234` gezogen. Toter Parameter — verwirrend, aber funktional folgenlos.

**B11 — MP3-Passthrough scheitert an ID3v2-Tags und VBR-Headern** — `Media/Mp3PassthroughReader.cs:43-44`: Der Reader erwartet ab Byte 0 einen gültigen MPEG-Frame-Header; jede real-weltliche MP3 mit ID3v2-Prolog wirft sofort `InvalidOperationException`. Kein Resync auf das nächste Syncword.

**B12 — Recording-Verschlüsselung lädt die ganze Datei in den RAM** — `Media/AesGcmRecordingEncryptionProvider.cs:63-64`: `File.ReadAllBytesAsync` + gleichgroßes Ciphertext-Array = 2× Dateigröße im Speicher; lange Aufnahmen (Stunden WAV) sind hunderte MB. Zudem wird `_key` nie genullt (Z. 25) — für einen Langzeit-Provider im Prozess akzeptabel, aber erwähnenswert. Kein Decrypt-Gegenstück im Bereich (einweg by design?).

**B13 — ffmpeg-Prozess-Lifecycle** — `Media/FfmpegProcessRunner.cs:49`: Bei Cancellation wirft `WaitForExitAsync`, der ffmpeg-Prozess läuft aber weiter (kein `Kill`); `IsAvailable` (Z. 89-90) lässt bei Timeout von `WaitForExit(1000)` ebenfalls einen laufenden Prozess zurück und `ExitCode` wirft dann (vom catch-all geschluckt).

**B14 — `Mp3TranscodingWriter`: sync-over-async im Konstruktor und werfendes `DisposeAsync`** — `Media/Mp3TranscodingWriter.cs:38-42` blockiert mit `.GetAwaiter().GetResult()` (Deadlock-Risiko unter SynchronizationContext); `:98-99` wirft aus `DisposeAsync` (`InvalidOperationException` bei fehlgeschlagenem Encode) — legitim, um Datenverlust sichtbar zu machen, aber in `await using`-Ketten leicht verschluckbar/fatal. Das ffmpeg-Encode in Dispose läuft zudem ohne CancellationToken.

**B15 — `WavAudioFileReader.ParseHeader`: partielle Reads** — `Media/WavAudioFileReader.cs:117`: `stream.Read(fmt) != chunkSize` behandelt einen legalen Partial-Read als Fehler („Invalid WAV fmt chunk"); bei lokalen FileStreams selten, aber nicht garantiert.

**B16 — SAN-Parsing über `X509Extension.Format` ist plattform-/lokalisierungsfragil** — `Security/SipDomainCertificateValidator.cs:114-131`: Das String-Parsing der formatierten SAN-Ausgabe (Komma-/Zeilen-Split, Präfixlisten `uri:`/`url=`/`dns name=` …) ist im Code selbst als plattformabhängig dokumentiert; eine SAN-URI mit Komma in Parametern würde falsch zerteilt. Robuster wäre ASN.1-Parsing (`AsnDecoder`/`X509SubjectAlternativeNameExtension` ab .NET 9).

**B17 — `TlsConfiguration.GetCertificate` nicht threadsicher** — `Security/TlsConfiguration.cs:55-65`: unsynchronisiertes Lazy-Load; Doppel-Load bei Race harmlos, aber zwei `X509Certificate2`-Instanzen möglich (eine leakt, `X509Certificate2` ist IDisposable). `SYSLIB0057` wird unterdrückt (Z. 69-71) statt auf `X509CertificateLoader` zu migrieren.

**B18 — RTX-PT-Zuweisung kann den gültigen PT-Bereich verlassen** — `Sdp/VideoCodecCatalog.cs:105-113`: `nextPt = max(videoPT)+1` ohne Obergrenze 127; mit hohen Video-PTs (z. B. 126/127) entstünden ungültige PTs > 127. Mit den Default-PTs 96/97 nicht auslösbar.

**B19 — Scheduler-Details** — `Common/Timing/ScheduledActionScheduler.cs`: (a) `Cancel` (Z. 63-79) released das Semaphor bei jedem Cancel — Signalzähler kann anwachsen und den Loop zu Leer-Iterationen wecken (nur Effizienz); (b) stornierte Einträge werden erst beim Erreichen des Queue-Kopfs entfernt (Z. 131-135, 158-163) — Tombstone-Speicher bis Fälligkeit; (c) Callbacks laufen sequentiell auf dem einen Worker — ein blockierender Callback verzögert alle SIP-Timer.

**B20 — Parser-Toleranz als RFC-Abweichung** — `Sdp/Parsing/SdpSessionParser.cs:22-23`: fehlende `o=`/`c=`-Zeilen defaulten stillschweigend auf `127.0.0.1`; Pflichtzeilen `v=`, `s=`, `t=` werden weder verlangt noch geprüft. Bewusst tolerant, aber ein Offer ohne jegliche `c=`-Zeile ergibt Loopback statt Ablehnung — `TryParseMediaParameters` würde dann Medien an 127.0.0.1 adressieren (praktisch: Call-Aufbau gegen Loopback statt 488).

**B21 — Kosmetik** — `Common/Network/LocalEndPointAdvertisementResolver.cs:23-28`: massive Trailing-Whitespace in den Bedingungszeilen; `Sdp/Parsing/SdpSessionParser.cs:394-397` leere Kommentar-Sektion („Mutable media builder…") als Überbleibsel der MediaBuilder-Extraktion; `Sdp/Parsing/SdpSessionSerializer.cs:154-160` doppelte/leere Sektionsüberschriften.

### Explizit dokumentierte, bewusste Lücken (kein Bug, aber Follow-ups)
- Kein volles ICE im SIP-/WebRtcRemoteEndPoint-Pfad (`WebRtc/WebRtcRemoteEndPoint.cs:14-17`).
- mDNS-Kandidaten werden ignoriert (`WebRtc/WebRtcPeerConnection.cs:503-508`).
- TCP/TLS-TURN-Gathering fehlt (`WebRtc/WebRtcPeerConnection.cs:590-591`); Answerer-seitige Relay-Adoption teils Follow-up (Z. 329-334).
- Video-m-Line ohne eigene Kandidaten im SIP-Pfad (`SdpOfferAnswerNegotiator.cs:115-118`); NACK-Retransmission ist angekündigt, aber nur PLI implementiert (`VideoCodecCatalog.cs:69-70`); per-PT-Feedback-Mirroring deferred (`VideoCodecCatalog.cs:83-86`); Simulcast-Komma-Alternativen deferred (`WebRtcSessionFactory.cs:293-296`); Answerer-Simulcast Follow-up (`WebRtcSessionFactory.cs:290-292`).
- Keine echten TODO-Marker im Bereich (kein `TODO`/`FIXME`/`HACK` gefunden); die Codebasis nutzt stattdessen die obigen strukturieren „follow-up"-Kommentare und HARD-*-Referenzen (HARD-C6 Send/Dispose-Race, HARD-G3 Observability, HARD-S1 SDES-auf-DTLS-Guard).

### Gesamteinschätzung
Der Bereich ist ungewöhnlich reif: konsistente RFC-Verweise, fail-closed-Security, dokumentierte Threading-Verträge und saubere Modulschnitte (insbesondere das Relay-Seam). Die gefundenen Risiken sind überwiegend Randfälle (B2, B3, B6, B7, B11, B16 sind die praxisrelevantesten); echte Korrektheitsfehler im Kernpfad (Offer/Answer, Keying, DTMF, Session-Aufbau) wurden nicht gefunden.



---

# Teil 5 — Core Application- & Domain-Schicht (~110 Dateien)


**Analysierter Bereich:** `src/Core/Application/` (ohne `Media/Ice`, `Media/Rtcp`), `src/Core/Domain/` (48 Dateien), `src/Core/Sdk/`, `src/Core/Properties/AssemblyInfo.cs` — insgesamt ~110 gelesene Dateien.

---

## 1. Überblick & Verantwortung

Das SDK folgt konsequent einer **hexagonalen Architektur (Ports & Adapters) mit DDD-Anleihen**:

- **Domain-Schicht** (`Core/Domain`): Reine Fachlogik ohne Infrastrukturabhängigkeiten (nur `Microsoft.Extensions.Logging` als Querschnitt). Zwei Aggregate: `Call` (Anruf-Lebenszyklus mit Zustandsautomat `CallStateRules`) und `PhoneLine` (SIP-Registrierung + Call-Erzeugung). Dazu Value Objects (`CallId`, `LineId`, `SipAddress`, `DtmfTone`, `SipCredentials`), reichhaltige immutable Parameter-Records (`CallMediaParameters`, `CallVideoParameters`) und Domain-Events als klassische .NET-`EventArgs`. Die Domain definiert selbst zwei **einwärtsgerichtete Ports** (`ICallChannel`, `ILineChannel`), die die SIP/RTP-Transportschicht abstrahieren, sowie `ICallRegistry` als Abhängigkeitsumkehr zur Application-Schicht.
- **Application-Schicht** (`Core/Application`): Use-Case-Orchestrierung — Call-/Line-Registries (`CallManager`, `PhoneLineManager`), Media-Session-Lebenszyklus (`CallMediaOrchestrator`), ICE-Agent (`CallIceAgent`), RTCP-Qualitätsüberwachung (`CallRtcpQualityMonitor`), Recording/Playback (`MediaManager` + `Sessions/*`), Komfort-Workflows (`SdkConvenienceOrchestrator`) und die **auswärtsgerichteten Ports** unter `Application/Ports/*` (Audio-Gerät, Video-Gerät, SDP-Negotiator, STUN/TURN, Datei-Codecs, Telemetrie).
- **`Core/Sdk`**: Reine öffentliche Konfigurations-DTOs für ICE (`IceConfiguration`, `IceOptions`, `IceServerConfiguration`, `IceServerType`, `IceTransport`) im Root-Namespace `CalloraVoipSdk` — kein Verhalten außer `IceOptions.ToConfiguration()`.
- **Verdrahtung**: `src/Client/Application/Facades/VoipClient.cs` (Composition Root) instanziiert `CallManager`, `MediaManager`, `CallMediaOrchestrator`, `PhoneLineManager` (mit Factory-Lambda, das `SipLineChannel` + `PhoneLine` baut) und `SdkConvenienceOrchestrator`; DI-Overrides sind für fast alle Ports möglich.

Design-Prinzipien, die durchgängig erkennbar sind: **transport-only** (das SDK codiert/decodiert kein Video und nur minimal Audio — Codecs sind austauschbare Ports), **fail-safe auf Echtzeitpfaden** (Frames droppen statt werfen), **Handler-Snapshot unter Lock** gegen Subscribe/Unsubscribe-Races, und dokumentierte Regressions-Marker (`HARD-E1`, `HARD-E2`, `HARD-R5`, `HARD-R6`) an Stellen behobener Bugs.

---

## 2. Architektur

### 2.1 Ports und ihre Implementierungen (per Grep verifiziert)

| Port (Application/Domain) | Implementierung(en) |
|---|---|
| `ICallChannel` (Domain, internal) | `Infrastructure/Sip/Adapters/SipCoreCallChannel.cs` |
| `ILineChannel` (Domain, internal) | `Infrastructure/Sip/Adapters/SipLineChannel.cs` |
| `ICallRegistry` (Domain, internal) | `Application/Calls/CallManager` (explizite Interface-Implementierung) |
| `ICallMediaSessionFactory` / `ICallMediaSession` | `Infrastructure/Rtp/RtpCallMediaSessionFactory.cs` |
| `IVideoMediaStream` | `Infrastructure/Rtp/VideoRtpStream.cs` |
| `IIceStunProbe` | `Infrastructure/Stun/Client/StunIceProbe.cs` |
| `IIceTurnRelayAllocator` | `Infrastructure/Turn/Client/TurnIceRelayAllocator.cs` |
| `IIceTelemetrySink` | `Infrastructure/Sip/Observability/SipIceTelemetrySink.cs` |
| `ISdpNegotiator` (public) | `Infrastructure/Sdp/SdpNegotiator.cs` |
| `IAudioDevice` (public) | `Infrastructure/Audio/SilenceAudioDevice`, `Audio/Linux/LinuxAudioDevice`, `Audio/Windows/WindowsAudioDevice`, `Audio/Headless/HeadlessAudioDevice` (via `IAudioDeviceProvider : IAudioDevice`) |
| `IVideoDevice` (public) | Keine Implementierung im Core — bewusst „fail closed", Codec-Paket liefert sie via DI |
| `IAudioFileCodecRegistry`/`IAudioFileCodec`/`IAudioFileReader`/`IAudioFileWriter` | `Infrastructure/Media/AudioFileCodecRegistry.cs`; Fallback `EmptyAudioFileCodecRegistry` (Application) |
| `IRtcpPacketCodec` (in `Media/Rtcp/Wire`, von mir nur als Konsument betrachtet) | `Infrastructure/Rtcp/Wire/RtcpPacketCodec.cs` |
| `IMixedMediaBus` (public) | Conferencing-Paket (außerhalb Core; `InternalsVisibleTo` für `CalloraVoipSdk.Conferencing.Tests`) |

Bemerkenswert: Die Domain-Ports `ICallChannel`/`ILineChannel` sind **internal** — Konsumenten sehen nur `ICall`/`IPhoneLine`. `ICallChannel` nutzt **Default Interface Methods** (`SetDtmfSendDelegate`, `DeliverInboundDtmf`, `RemoteAssertedIdentity`, `Diversion`) für rückwärtskompatible Erweiterung.

### 2.2 Call-Zustandsautomat

`Domain/Calls/CallState.cs` + `CallStateRules.cs` (zentrale, rein statische Übergangstabelle):

```
Idle ──► Dialing ──► Ringing ──► Connected ◄──► OnHold
  │         │           │            │    ╲       │
  │         └──►Connected            ▼     ╲──► Transferring ──► Connected
  └────────────────────────────► Terminated ◄────────┘ (alle Zustände außer selbst)
```

- `Terminated` ist absorbierend (`from == Terminated → false`).
- `Call.TransitionTo` (Call.cs:338–362) validiert gegen `CallStateRules`, ignoriert ungültige Übergänge still (Debug-Log), snapshottet den `StateChanged`-Handler **innerhalb des Locks** und disposed den Channel beim Erreichen von `Terminated`.
- Der Zustand liegt als `volatile int _stateInt` (lock-freies Lesen, Schreiben nur unter `_sync`).

### 2.3 Line-Zustandsautomat

`Domain/Lines/LineState.cs`: `Unregistered → Registering → Registered`, Verlust → `Reconnecting ⇄ Registering`, `RegistrationFailed` (retry möglich), `Failed` (endgültig: `MaxRetries` überschritten oder 401/403). **Anders als beim Call gibt es keine Regel-Tabelle** — `PhoneLine.TransitionTo` (PhoneLine.cs:155–166) dedupliziert nur; die Übergangslogik lebt vollständig im Infrastruktur-Kanal (`ILineChannel.StartRegistration`-Callbacks inkl. `onReconnecting`/`onReconnectFailed`).

### 2.4 Event-Modell

Drei Ebenen:
1. **Transport→Domain**: `CallChannelCallbacks` (Record aus 4 Delegates: `OnStateChange`, `OnDtmf`, `OnRemoteHold`, `OnTransferRequested`), einmalig via `ICallChannel.BindCallbacks` gebunden, bevor Events feuern können.
2. **Domain-Events** (public, `EventArgs`-basiert, alle mit internal-Konstruktor): pro Call `StateChanged`, `HoldStateChanged`, `DtmfReceived`, `TransferRequested` (mit synchronem Accept-Rückkanal via `args.Accept`), `QualitySnapshotChanged`, `IceConnectionStateChanged`; pro Line `StateChanged`, `IncomingCall`, `LineReconnecting`, `LineReconnectFailed`. Zusätzlich interne `Action`-Events auf `Call` für Video (`VideoCongestionChanged`, `VideoKeyFrameRequested`).
3. **Aggregations-Events** (Application): `CallManager.CallAdded/CallRemoved/CallStateChanged` (registry-weit), `PhoneLineManager.IncomingCall` (leitungsübergreifend).

Der Threading-Vertrag ist in `ICall.cs:9–34` ungewöhnlich ausführlich dokumentiert: Handler laufen synchron auf SDK-Threads (SIP-Signaling oder Media/RTCP), dürfen nicht blockieren; `TransferRequested` ist die dokumentierte Ausnahme (synchrone Entscheidung nötig).

---

## 3. Klassenkatalog

### 3.1 Domain/Calls (24 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `Call` (internal sealed class) | `Domain/Calls/Call.cs` | Aggregat für einen Anruf-Lebenszyklus. Besitzt Signaling-Zustandsübergänge, übersetzt Transport-Callbacks in Domain-Events, hält Quality-/RTP-/ICE-Snapshots und Video-Congestion-Zustand; leitet Audio-/Video-Frames und erweiterte SIP-Aktionen an den `ICallChannel` weiter. |
| `ICall` (public interface) | `Domain/Calls/ICall.cs` | Öffentliche Call-API: Properties (State, Direction, MediaParameters, Snapshots, P-Asserted-Identity, Diversion), 6 Events, Aktionen mit dualem Fehlerkontrakt (Kern-Lifecycle wirft; erweiterte In-Dialog-Aktionen liefern `CallActionResult`). |
| `ICallChannel` (internal interface) | `Domain/Calls/ICallChannel.cs` | Domain-Port zum SIP+RTP-Transport eines Calls: Answer/Hangup/Hold/Transfer/Reject/Redirect/INFO/OPTIONS/SUBSCRIBE/NOTIFY, Frame-Listener-Verwaltung, Send-Delegates (Audio/Video/DTMF), Event `MediaParametersNegotiated`. |
| `ICallRegistry` (internal interface) | `Domain/Calls/ICallRegistry.cs` | Minimalsicht der Domain auf die Call-Registry (`Register`, `Active`); von `CallManager` implementiert (Dependency Inversion). |
| `CallStateRules` (internal static) | `Domain/Calls/CallStateRules.cs` | Zentrale Übergangstabelle des Call-Zustandsautomaten. |
| `CallState` (public enum) | `Domain/Calls/CallState.cs` | Idle, Dialing, Ringing, Connected, OnHold, Transferring, Terminated. |
| `CallDirection` (public enum) | `Domain/Calls/CallDirection.cs` | Inbound/Outbound. |
| `CallId` (public record struct) | `Domain/Calls/CallId.cs` | GUID-basierter, typsicherer Call-Identifier. |
| `CallActionResult` (public sealed class) | `Domain/Calls/CallActionResult.cs` | Einheitliches Ergebnis erweiterter Call-Aktionen (Status, SIP-Code, Reason); Fabriken `Success`/`Failure`. |
| `CallActionStatus` (public enum) | `Domain/Calls/CallActionStatus.cs` | Succeeded, Rejected, InvalidState, InvalidRequest, Canceled, Failed. |
| `CallAudioFrame` (internal record struct) | `Domain/Calls/CallAudioFrame.cs` | Ein codierter Audio-Frame (Payload, PT, Dauer in RTP-Einheiten) am Call-Boundary. |
| `CallVideoFrame` (internal record struct) | `Domain/Calls/CallVideoFrame.cs` | Ein codierter Video-Frame (Payload, PT, absoluter 90-kHz-RTP-Timestamp, Keyframe-Flag). |
| `CallChannelCallbacks` (internal record) | `Domain/Calls/CallChannelCallbacks.cs` | Bündel der Transport→Domain-Callbacks. |
| `CallMediaParameters` (public sealed record) | `Domain/Calls/CallMediaParameters.cs` | Ausgehandelte Medienparameter eines Legs: Endpunkte, RTCP-Mux, Codec/PT-Map, Telephone-Event-PT, komplette ICE-Metadaten, SRTP-Policy-Resultat, interne SDES-/DTLS-Schlüsseldaten, optional `Video`. Custom `ToString()` verhindert Key-Leak in Logs. |
| `CallVideoParameters` (public sealed class) | `Domain/Calls/CallVideoParameters.cs` | Video-m-line-Verhandlungsergebnis: PT, Codec, fmtp, RTX, NACK/PLI-Fähigkeiten, SDES-Keys, ICE-Credentials für das Video-5-Tupel, transport-cc-Extension-Id. |
| `CallIceCandidate` (public sealed class) | `Domain/Calls/CallIceCandidate.cs` | Ein ICE-Kandidat (Foundation, Component, Transport, Priority, Adresse, Typ, raddr/rport, Extensions). |
| `CallIceSnapshot` (public sealed record) | `Domain/Calls/CallIceSnapshot.cs` | Read-only-Snapshot des ICE-Establishments (Endzustand, gewähltes Paar, Nominierung, Endpunkte). |
| `CallIceState` (public enum) | `Domain/Calls/CallIceState.cs` | Disabled, Gathering, Gathered, Checking, Nominating, Connected, Disconnected (RFC-7675-Consent-Verlust, transient), Failed. |
| `CallQualitySnapshot` (public record struct) | `Domain/Calls/CallQualitySnapshot.cs` | Abgeleitete Qualitätsmetriken (Jitter, Loss lokal/remote, RTT, RTCP-Zähler, RTCP-XR-MOS LQ/CQ); `CreateEmpty`-Baseline. |
| `CallRtpStatistics` (public record struct) | `Domain/Calls/CallRtpStatistics.cs` | Rohe RFC-3550-Zähler (SSRCs, Pakete/Oktette, Loss, extended-highest-seq, Jitter in RTP-Einheiten) für Diagnose/Abrechnung. |
| `DialOptions` (public sealed class) | `Domain/Calls/DialOptions.cs` | Per-Call-Optionen: RingTimeout (30 s), DisplayName-, Proxy-, SRTP-Override (tri-state), Custom-Header. |
| `DtmfTone` (public record struct) | `Domain/Calls/DtmfTone.cs` | Validierter DTMF-Ton (0–9, *, #, A–D) mit RFC-4733-Code-Mapping in beide Richtungen. |
| `NetworkQuality` (public enum) | `Domain/Calls/NetworkQuality.cs` | Good/Fair/Poor — grobe Medienpfad-Gesundheit aus dem Congestion-Signal. |

### 3.2 Domain/Events (12 Dateien)

Alle: public sealed `EventArgs`-Klassen mit internal-Konstruktoren.

| Typ | Datei | Beschreibung |
|---|---|---|
| `CallActivityEventArgs` | `Events/CallActivityEventArgs.cs` | Call zur Registry hinzugefügt/entfernt. |
| `CallErrorEventArgs` | `Events/CallErrorEventArgs.cs` | Call-bezogener Fehler (Message, optional Call, Exception). Auffällig: **im analysierten Bereich nirgends geraised** — offenbar von der Fassade/Infra genutzt. |
| `CallIceConnectionStateChangedEventArgs` | `Events/CallIceConnectionStateChangedEventArgs.cs` | Laufende ICE-Zustandsänderung (Old/New/Call). |
| `CallQualitySnapshotChangedEventArgs` | `Events/CallQualitySnapshotChangedEventArgs.cs` | Neuer Qualitäts-Snapshot. |
| `CallStateChangedEventArgs` | `Events/CallStateChangedEventArgs.cs` | Old/New `CallState` + Call. |
| `DtmfReceivedEventArgs` | `Events/DtmfReceivedEventArgs.cs` | Ton, Dauer (ms), Call. |
| `HoldStateChangedEventArgs` | `Events/HoldStateChangedEventArgs.cs` | IsOnHold, ByRemoteParty, Call. |
| `IncomingCallEventArgs` | `Events/IncomingCallEventArgs.cs` | Eingehender, bereits klingelnder Call. |
| `LineReconnectFailedEventArgs` | `Events/LineReconnectFailedEventArgs.cs` | Endgültiges Re-Register-Scheitern (Reason, AttemptCount, Line). |
| `LineReconnectingEventArgs` | `Events/LineReconnectingEventArgs.cs` | Reconnect-Versuch Nr. n beginnt. |
| `LineStateChangedEventArgs` | `Events/LineStateChangedEventArgs.cs` | Old/New `LineState` + Line. |
| `TransferRequestedEventArgs` | `Events/TransferRequestedEventArgs.cs` | Inbound-REFER: TargetUri, Call, settable `Accept`. |

### 3.3 Domain/Lines (11 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `PhoneLine` (internal sealed class) | `Lines/PhoneLine.cs` | Aggregat: registriert via `ILineChannel`, erzeugt Calls (Outbound `DialAsync` mit `PrepareOutboundChannel`→`Call`-Bindung→INVITE; Inbound via `SetInboundHandler`), erzwingt `_maxCalls` per eigenem `Interlocked`-Zähler, benachrichtigt `onCallCreated` (Media-Orchestrator-Hook). |
| `IPhoneLine` (public interface) | `Lines/IPhoneLine.cs` | Öffentliche Line-API: LineId, Account, State, 4 Events, `DialAsync`, `UnregisterAsync`. |
| `ILineChannel` (internal interface) | `Lines/ILineChannel.cs` | Port für Registrierung + Outbound-Dial-Bootstrap; `StopRegistration()` (best-effort, Dispose-Pfad) vs. `StopRegistrationAsync` (awaited REGISTER Expires:0, „HARD-E1"). |
| `LineId` (public record struct) | `Lines/LineId.cs` | GUID-Identifier. |
| `LineState` (public enum) | `Lines/LineState.cs` | Unregistered, Registering, Registered, Reconnecting, RegistrationFailed, Failed. |
| `ReregisterFailReason` (public enum) | `Lines/ReregisterFailReason.cs` | MaxRetriesExceeded, AuthenticationFailed. |
| `ReregisterOptions` (public sealed class) | `Lines/ReregisterOptions.cs` | Auto-Reregister-Politik: Retries (0 = unbegrenzt), Exponential-Backoff 2 s–60 s, RefreshRatio 0.8, MinRefreshInterval 15 s, `MaxCorrectiveReregistrations` (NAT-Kontakt-Korrektur-Kappe). |
| `SipAccount` (public sealed class) | `Lines/SipAccount.cs` | Kontokonfiguration: Credentials, Server, Transport/Port (`EffectivePort`-Ableitung), NAT-Felder (`PublicSipHost/Port`, `PublicMediaHost`), Trunk-Inbound-Politik (`InboundNumbers`-Whitelist, `AcceptTrunkInbound`), `Reregister`. |
| `SipAddress` (public record struct) | `Lines/SipAddress.cs` | Validierte SIP-URI mit `sip:`-Normalisierung, User/Host-Parsing. |
| `SipCredentials` (public sealed record) | `Lines/SipCredentials.cs` | Username/Password/Realm mit Validierung. |
| `SipTransport` (public enum) | `Lines/SipTransport.cs` | Udp, Tcp, Tls, Ws, Wss. |

### 3.4 Domain/Security (2 Dateien, kurz)

| Typ | Datei | Beschreibung |
|---|---|---|
| `SrtpPolicy` (public enum) | `Security/SrtpPolicy.cs` | Disabled / Optional / Required. |
| `SrtpDecisionReasonCodes` (public static class) | `Security/SrtpDecisionReasonCodes.cs` | 8 stabile String-Reason-Codes (`srtp.negotiated`, `srtp.required.remote_no_srtp`, …) für Telemetrie und `CallMediaParameters.SrtpDecisionReasonCode`. |

### 3.5 Application/Calls (3 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `CallManager` (public sealed class) | `Calls/CallManager.cs` | Registry der Live-Calls (`ConcurrentDictionary<CallId, Call>`); implementiert `ICallRegistry` explizit (hält `Register` internal); entfernt Calls automatisch bei `Terminated` und meldet `CallAdded/CallRemoved/CallStateChanged` weiter. |
| `ICallManager` (public interface) | `Calls/ICallManager.cs` | Öffentliche Registry-Sicht (Events, `Active`, `Find`). |
| `SrtpPolicyEvaluator` (internal static) + `ResolvedSrtpPolicy` (internal record struct) | `Calls/SrtpPolicyEvaluator.cs` | Reine Policy-Funktionen: `ResolveEffectivePolicy` (global vs. `DialOptions.UseSrtp`-Override), `IsPolicyViolation`, `ResolveReasonCode`. Von Infra konsumiert, damit Policy-Regeln nicht dort dupliziert werden. |

### 3.6 Application/Lines (2 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `PhoneLineManager` (public sealed class) | `Lines/PhoneLineManager.cs` | Line-Registry mit injizierter Factory `Func<SipAccount, PhoneLine>`; `Register` startet sofort die SIP-Registrierung, aggregiert `IncomingCall`; `UnregisterAsync` (awaited De-Register + Dispose); `Dispose` disposed alle Lines. |
| `IPhoneLineManager` (public interface) | `Lines/IPhoneLineManager.cs` | Öffentliche Sicht (Register/UnregisterAsync/All/IncomingCall). |

### 3.7 Application/Convenience (3 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `SdkConvenienceOrchestrator` (internal sealed) + `LineConnectOutcome`/`LineConnectStatus`/`CallConnectOutcome`/`CallConnectStatus` | `Convenience/SdkConvenienceOrchestrator.cs` | Happy-Path-Helfer: `RegisterAndWaitAsync` (TCS-basiertes Warten auf Registered/Unregistered/optional RegistrationFailed mit Timeout/Cancel), `DialAndWaitUntilConnectedAsync` (Warten auf Connected/OnHold/Terminated, optional Auto-Hangup bei Timeout/Cancel), `Attach/DetachDefaultAudio/VideoAsync` (nur eine aktive Default-Route; verdrängt Attachments anderer Calls). |
| `DefaultAudioCallAttachment` (internal sealed) | `Convenience/DefaultAudioCallAttachment.cs` | Lebenszyklus-Wrapper: abonniert `StateChanged`, verbindet bei Connected/OnHold Receiver+Sender+`IAudioDevice` (Parameter aus `MediaParameters` oder Default) und räumt sich bei `Terminated` selbst ab. |
| `DefaultVideoCallAttachment` (internal sealed) | `Convenience/DefaultVideoCallAttachment.cs` | Spiegelbild für Video mit `IVideoDevice` (Codec-Paket der Anwendung). |

### 3.8 Application/Media — Top-Level (43 Dateien, ohne Ice/Rtcp)

**Orchestrierung & ICE:**

| Typ | Datei | Beschreibung |
|---|---|---|
| `CallMediaOrchestrator` (internal sealed) | `Media/CallMediaOrchestrator.cs` | Herzstück: pro Call an `MediaParametersNegotiated` gekoppelt; erstellt/verkabelt/zerstört `ICallMediaSession` + `CallRtcpQualityMonitor`, führt ICE-Auswahl asynchron aus, überwacht Inbound-RTP-Stille (NAT-BYE-Fallback-Hangup), spiegelt Quality/RTP/ICE/Video-Congestion-Zustand in das `Call`-Aggregat. |
| `ActiveMediaEntry` (internal record) | `Media/ActiveMediaEntry.cs` | Bündel aus Session, QualityMonitor, Channel und allen 10+ Handler-Referenzen für sauberes Unwiring. |
| `MediaActivity` (internal sealed) | `Media/MediaActivity.cs` | Mutabler Inbound-Aktivitäts-Tracker (LastReceived, LastActivityUtc, `HungUp`-Interlocked-Once-Guard). |
| `MediaSupervisionOptions` (internal record) | `Media/MediaSupervisionOptions.cs` | `InboundMediaTimeout` (15 s Default, ≤0 = aus), `HangupHeldCallOnSilence` (false). |
| `CallIceAgent` (internal sealed) | `Media/CallIceAgent.cs` | ICE-Agent: Kandidaten-Gathering (Host/srflx via `IIceStunProbe` über das geteilte Media-Socket/relay via `IIceTurnRelayAllocator`, inkl. Video-5-Tupel), Konnektivitätschecks über `IceConnectivityScheduler` (Ice-Unterordner), reguläre Nominierung (USE-CANDIDATE), Telemetrie-Publishing. |
| `ICallIceAgent` (internal interface) | `Media/ICallIceAgent.cs` | Port: `BuildLocalDescriptionAsync`, `SelectCandidatePairAsync`. |
| `CallIceLocalDescription` (internal sealed) | `Media/CallIceLocalDescription.cs` | Generierte lokale ufrag/pwd/Options + Audio-/Video-Kandidaten. |
| `CallIceNegotiationState` (internal enum) | `Media/CallIceNegotiationState.cs` | Interner ICE-Zustands-Spiegel (Disabled…Failed). |
| `CallIceSelectionResult` (internal sealed) | `Media/CallIceSelectionResult.cs` | Ergebnis inkl. `HasSelectedPair`, `Nominated`, Endpunkten, Kandidaten und stabilem `ReasonCode`. |
| `CallIceSnapshotFactory` (internal static) | `Media/CallIceSnapshotFactory.cs` | Pure Projektion internes Selektionsergebnis → öffentlicher `CallIceSnapshot`. |

**RTCP/Qualität:**

| Typ | Datei | Beschreibung |
|---|---|---|
| `CallRtcpQualityMonitor` (internal sealed) | `Media/CallRtcpQualityMonitor.cs` | RTCP-Laufzeitkomponente je Session: SR/RR+SDES-Compound-Reports (5-s-`PeriodicTimer`), eigener UDP-Socket im Non-Mux-Fall, LSR/DLSR-RTT-Berechnung, RTCP-XR-MOS-Extraktion, opake CNAME (RFC 7022), publiziert `CallQualitySnapshot` und füttert den RTT-Hint in den Jitter-Buffer zurück. |
| `CallMediaRtpSnapshot` (internal record struct) | `Media/CallMediaRtpSnapshot.cs` | Sender-/Receiver-Zähler für SR/RR-Erzeugung. |
| `CallMediaRuntimeMetrics` (internal record struct) | `Media/CallMediaRuntimeMetrics.cs` | 13 Empfangspfad-Metriken (Queue-/Drop-/PLC-Zähler, Jitter, adaptives Delay, RTT). |
| `CallRtpStatisticsFactory` (internal static) | `Media/CallRtpStatisticsFactory.cs` | Pure Projektion Snapshot → öffentliche `CallRtpStatistics`. |

**Session-Ports & Public Media-API:**

| Typ | Datei | Beschreibung |
|---|---|---|
| `ICallMediaSession` (internal) | `Media/ICallMediaSession.cs` | RTP-Session-Port: Start/SendFrame/SendDtmf/RTT-Hint/Snapshots/RTCP-Mux-Datagramme; Events Frame/Dtmf/Metrics/RtcpMux/`MediaConsentLost`/`MediaConnectivityDegraded`/`MediaConnectivityRecovered`; `Video`-Substream via DIM. |
| `ICallMediaSessionFactory` (internal) | `Media/ICallMediaSessionFactory.cs` | Factory-Port (Infra: `RtpCallMediaSessionFactory`). |
| `IVideoMediaStream` (internal) | `Media/IVideoMediaStream.cs` | Video-Substream: serialisiertes `SendFrameAsync`, reassemblierte `FrameReceived`, `KeyFrameRequested` (PLI/FIR), `RecommendedBitrateBps`/`NetworkQuality`/`CongestionUpdated` (transport-cc). |
| `IMediaManager` (public) / `MediaManager` (public sealed) | `Media/IMediaManager.cs`, `Media/MediaManager.cs` | Fabrik + Einstieg für Receiver/Sender (Audio+Video), `MediaConnector`, Call-/Konferenz-Recording und -Playback; trackt aktive Sessions und räumt sie bei Stopped/Faulted aus. |
| `IMediaReceiver`/`MediaReceiver` (public) | `Media/IMediaReceiver.cs`, `Media/MediaReceiver.cs` | Per-Call-Tap für Inbound-Audio; Subscriber-Fault-Isolation via `GetInvocationList` mit try/catch; sorgfältiges Attach/Detach-Race-Handling (Rollback-Fenster nach Attach). |
| `IMediaSender`/`MediaSender` (public) | `Media/IMediaSender.cs`, `Media/MediaSender.cs` | Outbound-Audio-Injektion; droppt Frames außerhalb Connected/OnHold statt zu werfen; `GetPayloadArray` vermeidet Kopien wenn möglich. |
| `IVideoReceiver`/`VideoReceiver`, `IVideoSender`/`VideoSender` (public) | `Media/(I)Video*.cs` | Video-Pendants; `VideoSender` verwaltet zusätzlich die Subscription auf die internen Call-Events (`VideoCongestionChanged`, `VideoKeyFrameRequested`) und re-publiziert sie als `RecommendedBitrateChanged`/`KeyFrameRequested`. |
| `MediaConnector` (public sealed) | `Media/MediaConnector.cs` | Verbindet Receiver→Sender (`Connect`) bzw. bidirektional (`CrossConnect`); Kapazität 256. |
| `MediaConnection` (internal sealed) | `Media/MediaConnection.cs` | Gepufferte Weiterleitung: bounded Channel (DropOldest), Pump-Task, differenzierte Fehlerbehandlung beim Forward. |
| `CompositeDisposable` (internal sealed) | `Media/CompositeDisposable.cs` | Zwei Disposables, einmalige deterministische Freigabe. |
| `MediaFrame` / `VideoFrame` (public record structs) | `Media/MediaFrame.cs`, `Media/VideoFrame.cs` | Öffentliche codierte Frame-Verträge (Audio: Dauer; Video: absoluter RTP-Timestamp + Keyframe-Flag). |
| `MediaFrameReceivedEventArgs` / `VideoFrameReceivedEventArgs` / `VideoBitrateRecommendationEventArgs` (public sealed) | `Media/*EventArgs.cs` | Event-Payloads der Public-Media-API. |
| `IMixedMediaBus` (public) | `Media/IMixedMediaBus.cs` | PCM16-Misch-Bus-Abstraktion (Konferenz): `SubscribeMixedFrames`, `InjectPlaybackFrameAsync`. |
| `IRecordingSession` / `IPlaybackSession` (public) | `Media/IRecordingSession.cs`, `Media/IPlaybackSession.cs` | Öffentliche Session-Handles (State, Pause/Resume/Stop, StateChanged/Error, OutputFiles bzw. SourceFilePath). |
| `IRecordingEncryptionProvider` (public) | `Media/IRecordingEncryptionProvider.cs` | Optionale Verschlüsselung finalisierter Aufnahmedateien. |
| `MediaSessionState` (public enum) | `Media/MediaSessionState.cs` | Running, Paused, Stopped, Faulted (Stopped/Faulted terminal — von `TransitionTo` beider Sessions erzwungen). |
| `MediaSessionStateChangedEventArgs` / `MediaSessionErrorEventArgs` (public sealed) | `Media/MediaSession*EventArgs.cs` | Session-Übergangs- bzw. Fehler-Payloads. |
| `AudioFileFormat` (public enum) | `Media/AudioFileFormat.cs` | Wav, Mp3. |
| `RecordingOptions` / `PlaybackOptions` / `PlaybackRequest` (public sealed) | `Media/RecordingOptions.cs` u. a. | Konfiguration: Rotation, Silence-Skip, Encryption; Loop/StartPaused/FixedFrameDelay; Datei+Format+SampleRate. |
| `EmptyAudioFileCodecRegistry` (internal sealed) | `Media/EmptyAudioFileCodecRegistry.cs` | Null-Objekt-Registry (liefert nie einen Codec). |

### 3.9 Application/Media/Sessions (17 Dateien)

| Typ | Datei | Beschreibung |
|---|---|---|
| `AudioPayloadTranscoder` (internal static) | `Sessions/AudioPayloadTranscoder.cs` | Baut Transcoding-Pläne Wire-Codec ↔ Datei (WAV=PCM16, MP3-Passthrough); Codec-Erkennung über Namen und statische PTs; Opus stateful pro Leg. |
| `AudioPayloadTranscodingPlan` (internal sealed) | `Sessions/AudioPayloadTranscodingPlan.cs` | `CodecContext` + Delegates `ToFileFrame`/`FromFileFrame`. |
| `BridgeAudioTranscoder` (internal sealed) | `Sessions/BridgeAudioTranscoder.cs` | µ-law-Tap für 8-kHz-native Wire-Codecs (PCMU passthrough, PCMA/Opus transkodiert); G.722 bewusst nicht (Resampler fehlt, Warnung). |
| `CallPlaybackFrameSink` / `ConferencePlaybackFrameSink` (internal sealed) | `Sessions/*PlaybackFrameSink.cs` | Sink-Adapter: Call (via `IMediaSender`) bzw. Konferenzbus. |
| `CallRecordingFrameSource` / `ConferenceRecordingFrameSource` (internal sealed) | `Sessions/*RecordingFrameSource.cs` | Quellen-Adapter: Call-Tap (via `IMediaReceiver`) bzw. Mix-Bus-Subscription. |
| `IPlaybackFrameSink` / `IRecordingFrameSource` (internal) | `Sessions/I*.cs` | Sink-/Source-Abstraktionen mit Target-/SourceToken. |
| `OpusPayloadCodec` (internal sealed) | `Sessions/OpusPayloadCodec.cs` | Opus (RFC 7587) via Concentus (managed); mono, konfigurierbare PCM-Rate (48 kHz Default, 8 kHz für Bridge); stateful je Richtung. |
| `OpusDeviceCodec` (internal sealed) | `Sessions/OpusDeviceCodec.cs` | Geräte-Adapter mit Encode-Akkumulator (960-Sample-Frames), Decode-Passthrough; lockfrei per Richtungs-Threading-Vertrag. |
| `PayloadCodecKind` (internal enum) | `Sessions/PayloadCodecKind.cs` | Pcm16, Pcmu, Pcma, Mp3, G722, ComfortNoise, Unknown, Opus. |
| `PcmG711Codec` (internal static) | `Sessions/PcmG711Codec.cs` | Handgeschriebene µ-law/A-law-Encode/Decode-Tabellenlogik. |
| `PcmG722Codec` (internal static) | `Sessions/PcmG722Codec.cs` | G.722 via NAudio; **erzeugt pro Frame einen frischen `G722CodecState`** (siehe Befund 6.7). |
| `PlaybackSession` (internal sealed) | `Sessions/PlaybackSession.cs` | Loop-Task liest Datei-Frames, transkodiert, sendet an Sink mit Pacing (`FixedFrameDelay` oder Frame-Delay); Pause via 25-ms-Polling; Loop-Option. |
| `RecordingSession` (internal sealed) | `Sessions/RecordingSession.cs` | Bounded-Channel (512, DropOldest) → Writer-Loop; Datei-Rotation nach Bytes, Silence-Skip, optionale Post-Encryption mit Plaintext-Löschung. |
| `RecordingFileNamingStrategy` (internal static) | `Sessions/RecordingFileNamingStrategy.cs` | Deterministische Dateinamen `prefix-target[-utc]-partNNNN.ext` mit Sanitizing. |

### 3.10 Application/Ports (20 Dateien)

**Audio** (`Ports/Audio/`): `IAudioDevice` (Connect(receiver, sender, parameters)/Disconnect), `IAudioDeviceRuntimeControl` (Hot-Switch, Mute, Volume 0..2, Format), `AudioConnectionParameters` (Codec-Normalisierung; G.722-Sonderfall: SampleRate 16 kHz bei 8-kHz-RTP-Clock per RFC 3551), `AudioDeviceDescriptor`, `AudioDeviceFormat` (8 kHz/16 bit/mono Default), `AudioDeviceRuntimeSnapshot` (inkl. Playback-Queue-Tiefe/Drops).

**Connectivity** (`Ports/Connectivity/`, alle internal): `IIceStunProbe` (srflx-Discovery über geteiltes Socket; Konnektivitätscheck mit PRIORITY/Rolle/Tie-Breaker/USE-CANDIDATE), `IIceTurnRelayAllocator` + `IceRelayAllocation` (RelayedEndPoint, MappedEndPoint, Lifetime), `IIceTelemetrySink` + `IceTelemetryEvent` (strukturiertes Event mit Attributes-Map).

**Media** (`Ports/Media/`, internal): `IAudioFileCodec` (Reader/Writer-Fabrik), `IAudioFileCodecRegistry`, `IAudioFileReader` (frame-weise, null=EOF), `IAudioFileWriter` (BytesWritten für Rotation), `AudioFileCodecContext` (PT/ClockRate/SampleRate/SamplesPerFrame/CodecName), `AudioFileFrame` (Frame + Delay-Hint).

**Sdp** (`Ports/Sdp/`, public): `ISdpNegotiator` (BuildDefaultSdp, TryBuildNegotiatedAnswer, TryParseMediaParameters ×2 — Overload mit Optionen als DIM für Kompatibilität, IsRemoteHoldSdp); `SdpMediaNegotiationOptions` mit Unterobjekten `SdpIceNegotiationOptions`, `SdpDtlsNegotiationOptions`, `SdpVideoNegotiationOptions` — deckt SDES-/DTLS-Offer-Steuerung, Codec-Präferenzen, Session-Id/-Version (RFC 3264 §5) und Rekey-Vermeidung bei Hold-Re-Offers ab.

**Video** (`Ports/Video/`, public): `IVideoDevice` (Codec-Paket-Seam: Capture+Encode+Decode+Render), `VideoConnectionParameters` (PT/Codec/90-kHz-Clock; bewusst ohne Auflösung/Framerate).

### 3.11 Sdk (5 Dateien) & AssemblyInfo

- `IceConfiguration` (immutable), `IceOptions` (mutable DI-Variante mit `ToConfiguration()`), `IceServerConfiguration` (Host/Port/Transport/Credentials), `IceServerType` (Stun/Turn), `IceTransport` (Udp/Tcp/Tls). Namespace ist `CalloraVoipSdk` (Root), nicht `…Core.Sdk`.
- `Properties/AssemblyInfo.cs`: 10 `InternalsVisibleTo`-Einträge — Tests (5), Performance (2), sowie **`CalloraVoipSdk.Client`, `CalloraVoipSdk.Audio.Linux`, `CalloraVoipSdk.Audio.Windows`, `CalloraVoipSdk.InteropHarness`**. Die internen Typen (inkl. `Call`, `PhoneLine`, Orchestratoren) sind damit de facto eine SDK-interne Cross-Assembly-API.

---

## 4. Zentrale Abläufe

### 4.1 Abgehender Anruf (Application-Sicht)

1. `PhoneLine.DialAsync` (PhoneLine.cs:67–96): Guard `State == Registered` und `_maxCalls`; `_channel.PrepareOutboundChannel(options)` liefert den `ICallChannel` **ohne** INVITE; `CreateCall` baut das `Call`-Aggregat (bindet sofort `CallChannelCallbacks`) und ruft `onCallCreated` → **`CallMediaOrchestrator.AttachCall`** abonniert `MediaParametersNegotiated`.
2. `_callRegistry.Register(call)` (CallManager) → `CallAdded`, dann `call.TransitionTo(Dialing)`.
3. `_channel.StartOutboundDialAsync` sendet INVITE; bei Exception → `Terminated` + rethrow.
4. Infra meldet Fortschritt über `OnStateChange` → `TransitionTo(Ringing/Connected/Terminated)`.
5. Nach SDP-Abschluss feuert der Kanal `MediaParametersNegotiated` → `CallMediaOrchestrator.OnMediaParametersNegotiated` (CallMediaOrchestrator.cs:123–153): ohne ICE synchron `SetUpMediaSession`; mit ICE `Task.Run` → `ResolveIceCandidatePairAsync` (Checks + Nominierung, Snapshot/`IceConnectionState` auf den Call, `with`-Kopie der Parameter mit den selektierten Endpunkten — HARD-R5) → `SetUpMediaSession`.
6. `SetUpMediaSession` (155–270): setzt `MediaParameters` auf den Call, räumt eine evtl. Alt-Session (re-INVITE) ab, erzeugt Session + `CallRtcpQualityMonitor`, verdrahtet: Inbound-Frames→`channel.DeliverInboundAudioFrame`, Send-Delegates (Audio/DTMF/Video), Consent-Events→`SetIceConnectionState`, Video-Congestion/Keyframe→Call, und startet Session + Monitor asynchron (`StartSessionAsync`).
7. Convenience-Variante: `SdkConvenienceOrchestrator.DialAndWaitUntilConnectedAsync` wartet per TCS auf Connected/OnHold/Terminated mit Timeout/Cancel-Semantik und optionalem Auto-Hangup.

### 4.2 Eingehender Anruf

`SipLineChannel` ruft den via `SetInboundHandler` registrierten `PhoneLine.HandleInbound(channel, remoteParty)` (PhoneLine.cs:105–118): bei `_maxCalls`-Überschreitung sofortiger, beobachteter Hangup (HARD-E2); sonst `CreateCall` (inkl. Orchestrator-Attach), `TransitionTo(Ringing)`, `Register`, dann `IncomingCall`-Event (von `PhoneLineManager` aggregiert). Der Konsument entscheidet über `call.AcceptAsync()` (nur Inbound+Ringing; `AnswerAsync` → `Connected`) oder `RejectAsync`/`RedirectAsync` (Result-basiert, nur Inbound+Ringing, danach `Terminated`).

### 4.3 Auflegen / Terminierung

- `HangupAsync` (Call.cs:119–125): idempotent bei `Terminated`; BYE via Channel, dann `TransitionTo(Terminated)` → Channel-Dispose (Call.cs:361).
- `CallManager.OnStateChanged` entfernt den Call aus der Registry und feuert `CallRemoved`; `CallMediaOrchestrator.OnCallStateChanged` (an `Calls.CallStateChanged` gebunden, VoipClient.cs:304) startet fire-and-forget `TeardownMediaAsync`: Metrics-Log, komplettes Unwiring, `DisposeAsync` von Monitor und Session.
- `PhoneLine` dekrementiert den per-Line-Zähler über einen `StateChanged`-Handler.
- Medien-Supervision: `CheckInboundMediaActivity` (CallMediaOrchestrator.cs:84–117) legt Connected-Calls nach 15 s ohne Inbound-RTP einmalig auf (NAT-Fallback für verlorene BYEs); Held-Calls nur bei Opt-in.
- Dispose-Pfade: `Call.Dispose` sendet Best-Effort-BYE mit Fault-Beobachtung per Continuation; `PhoneLine.Dispose` legt nur eigene Calls auf; `PhoneLineManager.Dispose` disposed alle Lines.

### 4.4 Hold / Resume / Transfer

- **Lokal**: `HoldAsync` (nur Connected) → `OnHold` + `HoldStateChanged(byRemote:false)`; `UnholdAsync` (nur OnHold) → `Connected`.
- **Remote**: `HandleRemoteHoldChanged` (Call.cs:377–383) aus SIP-Signalisierung; Zustandswechsel nur wenn passend, Event immer.
- **Blind Transfer**: `BlindTransferAsync` (Connected → `Transferring`); Erfolg → `Terminated`, sonst zurück zu `Connected` (10-s-Timeout an den Channel).
- **Attended Transfer**: `AttendedTransferAsync` mit Konsultations-Call (muss SDK-`Call` sein); gleiche Ergebnislogik, Rückgabe `bool`.
- **Inbound-REFER**: `RaiseTransferRequested` → `TransferRequested`-Event; der Handler setzt synchron `args.Accept`.

### 4.5 Line-Registrierung

`PhoneLineManager.Register(account)` → Factory baut `SipLineChannel`+`PhoneLine` → `line.StartRegistration()` verdrahtet `TransitionTo` plus Reconnect-Callbacks in den Kanal. Der Kanal betreibt Refresh (RefreshRatio 0.8, MinRefreshInterval), Auto-Reconnect mit Exponential-Backoff und meldet `Reconnecting`-Versuche bzw. endgültiges `Failed` (MaxRetries/401/403). `UnregisterAsync` awaited den REGISTER-Expires:0-Roundtrip (`StopRegistrationAsync`, HARD-E1) — im Gegensatz zum synchronen Best-Effort-`StopRegistration` des Dispose-Pfads.

### 4.6 Media-Session-Verwaltung (Recording/Playback)

`MediaManager` prüft Codec-Registry + baut über `AudioPayloadTranscoder` einen Plan (Call: aus `MediaParameters`; Konferenz: L16/PCM16), instanziiert Source/Sink-Adapter und die Session:
- **Recording**: Frames → bounded Channel → Writer-Loop mit Transcodierung, Silence-Skip, Datei-Rotation (`RotateAfterBytes` schließt den Writer; `EnsureWriterAsync` öffnet den nächsten Part), am Ende optionale Verschlüsselung + Plaintext-Löschung. Session wird bei Start-Fehler disposed.
- **Playback**: Reader-Loop mit Frame-Pacing und Loop-Option; Sink injiziert in Call (`MediaSender`, droppt außerhalb Connected/OnHold) oder Konferenzbus.
- Zustandsmaschine beider Sessions: Running/Paused ⇄, terminal Stopped/Faulted; `MediaManager` de-trackt bei terminalen Zuständen.

---

## 5. Threading- & Fehlerbehandlungsmodell

- **Synchrone Event-Dispatch-Philosophie**: Domain-Events laufen synchron auf dem auslösenden SDK-Thread (SIP-Signaling seriell; DTMF potenziell von zwei Threads; Quality/ICE auf Media-/RTCP-Threads). Der Vertrag („nicht blockieren, nicht werfen") ist auf `ICall`/`IPhoneLine` explizit dokumentiert.
- **Handler-Snapshot unter Lock**: `Call.TransitionTo`, `SetQualitySnapshot`, `SetIceConnectionState`, `SetVideoCongestion`, `RaiseVideoKeyFrameRequested` snapshotten den Delegate im Lock — verhindert Lost-Wakeups bei konkurrierendem Subscribe. `PhoneLine.TransitionTo` erzeugt Args im Lock, invoked außerhalb.
- **Locks + Interlocked-Mix**: `Call`/`PhoneLine`/Receiver/Sender nutzen ein privates `_sync`; Zähler und Once-Guards (`MediaActivity.HungUp`, `_started`, `_disposed`) via `Interlocked`; Registries via `ConcurrentDictionary`.
- **Async-Muster**: durchgehend `ConfigureAwait(false)`; `TaskCompletionSource` mit `RunContinuationsAsynchronously` im Convenience-Orchestrator; `PeriodicTimer` für RTCP; bounded `Channel<T>` mit `DropOldest` als Backpressure auf allen Media-Puffern (MediaConnection 256, RecordingSession 512); ICE-Auswahl per `Task.Run`, um den Signaling-Thread nicht zu blockieren (CallMediaOrchestrator.cs:130–152).
- **Fire-and-forget nur mit Beobachtung**: Best-Effort-Hangups werden über Continuations (`Call.Dispose`, Call.cs:632–642) bzw. `ObserveHangupAsync` (PhoneLine.cs:143–153) geloggt statt als unobserved Exceptions zu enden. Ausnahmen: `OnCallStateChanged` (`_ = TeardownMediaAsync(...)` — intern try/catch) und die Discards in `CallMediaOrchestrator.Dispose`.
- **Fehlerklassen-Dualismus**: Kern-Lifecycle wirft (InvalidOperation/Argument/OperationCanceled); erweiterte SIP-Aktionen mappen alle vorhersehbaren Fehler auf `CallActionResult` via `HandleCallActionException` (Call.cs:455–491) mit differenziertem Logging.
- **Echtzeitpfad-Härtung**: Sender droppen Frames bei falschem Call-State bzw. `InvalidOperationException` (Teardown-Race), Receiver isolieren Subscriber-Faults pro Delegate (`GetInvocationList` + try/catch), `MediaConnection.ForwardAsync` fängt vier Exception-Klassen differenziert.

---

## 6. Qualitätsbefunde

### Stärken

1. Sehr saubere Trennung Domain/Application/Infrastruktur; die Domain kennt keine Netz-/Crypto-Typen (SDES/DTLS-Material als Strings, dokumentiert in `CallMediaParameters.cs:159–192`).
2. Sicherheitsbewusstsein: Custom `ToString()` gegen Key-Leaks (`CallMediaParameters.cs:200–208`), opake RTCP-CNAME (Privacy, `CallRtcpQualityMonitor.cs:76–77`), interne init-only Key-Properties.
3. Robuste Event-Dispatch-Disziplin (Snapshot-unter-Lock durchgängig) und Subscriber-Fault-Isolation auf allen öffentlichen Media-Taps.
4. Regressions-Dokumentation im Code (HARD-E1/E2/E5/E7/R5/R6) — ungewöhnlich nachvollziehbare Bugfix-Historie, z. B. der `with`-Ausdruck statt Handkopie nach ICE (CallMediaOrchestrator.cs:446–455).
5. Praxisnahe Robustheit: Inbound-Media-Timeout als NAT-BYE-Fallback, Reconnect-Politik mit Corrective-Reregistration-Kappe, RTT-Rückkopplung in den Jitter-Buffer (CallRtcpQualityMonitor.cs:509–513).
6. Keine TODO/FIXME/HACK-Marker im gesamten Bereich (Grep-verifiziert).

### Potenzielle Bugs / Risiken

1. **Transfer kann im Zustand `Transferring` hängenbleiben** — `Call.BlindTransferAsync` (Call.cs:163–170) und `AttendedTransferAsync` (Call.cs:175–185): Wirft `_channel.*TransferAsync` eine Exception (Timeout/Cancel/Transportfehler), wird die abschließende `TransitionTo`-Zeile nie erreicht; der Call bleibt dauerhaft in `Transferring` (kein try/finally). Hold/DTMF/erneuter Transfer sind dann blockiert; nur Hangup führt heraus.
2. **`AttendedTransferAsync` ohne State-Guard** — Call.cs:175–185: Anders als `BlindTransferAsync` fehlt `GuardState(Connected)`. Aus z. B. `Ringing` wird der ungültige Übergang nach `Transferring` still ignoriert (CallStateRules), der Kanal-Transfer aber trotzdem ausgeführt und der Call anschließend nach `Terminated`/`Connected` bewegt — Inkonsistenz zwischen Guard-Logik und Regelwerk. Zudem erlauben die Rules `OnHold → Transferring`, aber keine der beiden Transfer-Methoden lässt Transfer aus Hold zu (BlindTransfer verlangt Connected, Attended prüft gar nicht) — Regeln und API-Guards sind nicht deckungsgleich.
3. **Race: Media-Session-Leak bei ICE + früher Terminierung** — `CallMediaOrchestrator.OnMediaParametersNegotiated` (140–152) startet die ICE-Auswahl im Hintergrund. Terminiert der Call, während die Checks laufen, entfernt `TeardownMediaAsync` (286–304) einen noch nicht existenten Eintrag; der ICE-Task ruft anschließend `SetUpMediaSession` auf und registriert Session+Monitor für einen bereits terminierten Call. Da kein weiteres `Terminated`-Event kommt, lebt die Session (inkl. RTP-Socket und RTCP-Loops) bis zum Orchestrator-Dispose. Geprüft wird nur `_disposed`, nie `call.State`.
4. **Doku-/Implementierungs-Diskrepanz „buffered and replayed"** — `ICall.cs:108–118` verspricht, dass `StateChanged`/`HoldStateChanged` gepuffert und nachgespielt werden, solange kein Handler abonniert ist. Das Domain-`Call` (TransitionTo, Call.cs:338–362) puffert nichts — ist kein Handler registriert, ist der Übergang verloren. Falls das Buffering im `SipCoreCallChannel` liegen sollte, greift es nicht für Zustände, die `Call` selbst setzt (`AcceptAsync`→Connected, `HoldAsync`→OnHold, …).
5. **Convenience-Registrierung: `Unregistered` als Sofort-Abbruch** — `SdkConvenienceOrchestrator.ShouldCompleteConnectWait` (364–367) wertet `Unregistered` als Endzustand, und Zeile 91–92 prüft den Zustand direkt nach `Register`. Meldet der Kanal den `Registering`-Übergang asynchron (Zustand noch `Unregistered`), liefert `RegisterAndWaitAsync` sofort `Failed`, obwohl die Registrierung gerade anläuft. Funktioniert nur, solange die Infrastruktur `Registering` synchron in `StartRegistration` setzt — eine fragile implizite Annahme.
6. **Default-Audio kann mit falschem Codec verbinden** — `DefaultAudioCallAttachment.ConnectIfNeeded` (99–101): Bei `Connected` wird `AudioConnectionParameters.Default` (PCMU/8 kHz) verwendet, wenn `MediaParameters` noch `null` ist. Da der Orchestrator die Parameter u. U. erst nach dem Connected-Event setzt (insbesondere im asynchronen ICE-Pfad), kann das Audio-Gerät mit PCMU öffnen, obwohl z. B. G.722/Opus verhandelt wurde; `_connected` verhindert eine spätere Korrektur (kein Re-Connect bei Parameter-Eintreffen).
7. **G.722 ist stateful, wird aber staatenlos transkodiert** — `PcmG722Codec.Decode/Encode` (PcmG722Codec.cs:19, 38) erzeugt **pro Frame** einen frischen `G722CodecState`. G.722 ist ein ADPCM-Codec mit Prädiktor-Zustand über Framegrenzen; Neuinitialisierung je 20-ms-Frame erzeugt hörbare Artefakte in Aufnahme/Playback. Der Kontrast zum korrekt behandelten Opus (Kommentar „stateful across frames … the plan owns one decoder/encoder instance", AudioPayloadTranscoder.cs:229–231) zeigt, dass das Problem grundsätzlich bekannt ist.
8. **PlaybackSession-Leak bei Cancellation** — `MediaManager.StartCallPlaybackCoreAsync`/`StartConferencePlaybackCoreAsync` (234–238, 274–278): Die Session startet ihren Loop bereits im Konstruktor und wird getrackt; wirft danach `ct.ThrowIfCancellationRequested()`, wird die laufende Session weder gestoppt noch disposed (Recording hat dafür ein `catch { DisposeAsync; throw; }` — Playback nicht) und spielt weiter, ohne dass der Aufrufer ein Handle bekommt.
9. **Recording: Writer-Race bei abgebrochenem Stop** — `RecordingSession.StopAsync` (146–171): Wird `ct` während `_writerLoop.WaitAsync(ct)` ausgelöst, läuft der Writer-Loop weiter, während der `finally`-Block parallel `_source.DisposeAsync` und `DisposeWriterAsync` ausführt — unsynchronisierter Zugriff auf `_writer` (Feld wird auch im Loop, 221–229, gelesen/beschrieben) und potenzielles Schreiben auf einen disposed Writer.
10. **`Call.Dispose` Race BYE vs. Channel-Dispose** — Call.cs:629–644: Der Best-Effort-`HangupAsync` wird fire-and-forget gestartet und **unmittelbar danach** `_channel.Dispose()` gerufen; je nach Channel-Implementierung kann das BYE auf einem bereits disposeden Kanal scheitern (wird zwar geloggt, aber das dokumentierte Ziel „BYE on dispose" ist nicht garantiert).
11. **Kein Re-INVITE-Ordering bei ICE** — Zwei schnell aufeinanderfolgende `MediaParametersNegotiated` (Re-INVITE) mit ICE laufen als unabhängige `Task.Run`-Ketten; die langsamere kann die neuere Session überschreiben (keine Sequenzierung pro CallId in `OnMediaParametersNegotiated`/`SetUpMediaSession`).
12. **`CallErrorEventArgs` ohne Producer im Kernbereich** — `Events/CallErrorEventArgs.cs` wird in Domain/Application nie instanziiert; entweder totes Modell oder rein fassaden-/infragetrieben (Konsistenzprüfung empfohlen).

### Kleinere Inkonsistenzen / Anmerkungen

- **Reihenfolge Register vs. Transition**: Outbound registriert vor `TransitionTo(Dialing)` (PhoneLine.cs:81–82), Inbound transitioniert vor `Register` (PhoneLine.cs:115–116). Folge: Der `Idle→Ringing`-Übergang eingehender Calls erscheint nie im aggregierten `CallManager.CallStateChanged` (Subscription erfolgt erst in `Register`) — für Konsumenten, die die Aggregation für Ringing-Erkennung nutzen, überraschend.
- `PhoneLineManager.Register` (Zeile 31) abonniert `IncomingCall` per Lambda und meldet es nie ab (kleines, praktisch harmloses Leak pro unregistrierter Line).
- `Call.HandleRemoteHoldChanged` feuert `HoldStateChanged` auch dann, wenn kein Zustandswechsel möglich war (z. B. Remote-Hold vor Connected).
- Performance-Nebenpunkte: `OpusPayloadCodec.Encode` kopiert via `pcm16.ToArray()` unnötig (OpusPayloadCodec.cs:73); `OpusDeviceCodec` nutzt `List<byte>` mit `AddRange(ToArray)`/`RemoveRange` auf dem Audio-Callback-Pfad (O(n)-Verschiebungen je Frame); `CallManager.Active` materialisiert bei jedem Zugriff eine Liste.
- `CallMediaOrchestrator.Dispose` (306–319) discardet `DisposeAsync()`-ValueTasks (`_ = …`) — nicht awaitete ValueTasks sind formal unsauber, Fehler beim Shutdown gehen ungeloggt verloren.
- `CallStateRules.CanTransition` behandelt `from == to` als erlaubt, der Aufrufer `TransitionTo` filtert den Fall vorher heraus — toter Zweig.
- `LineState` besitzt anders als `CallState` keine Regel-Tabelle; die Line-FSM-Korrektheit hängt vollständig an der Infrastruktur.
- Die breiten `InternalsVisibleTo`-Einträge (AssemblyInfo.cs:3–12, u. a. `CalloraVoipSdk.Client` und beide Audio-Pakete) machen `internal` im Core faktisch zu einer produktweiten API — Refactorings interner Typen sind entsprechend teuer.

---

**Fazit:** Eine überdurchschnittlich sorgfältig gebaute, dokumentierte und getestete Schichtung mit klarer Ports&Adapters-Struktur und konsistentem Threading-Modell. Die relevantesten Handlungspunkte sind der Transferring-Deadlock bei Transport-Exceptions (Befund 1/2), das ICE-Terminierungs-Race mit Session-Leak (3), der G.722-State-Fehler in der Transkodierung (7) und das Default-Audio-Codec-Race (6).



---

# Teil 6 — Client-Projekt, WebRTC-Fassade & Audio-Backends (~105 Dateien)


**Analysierter Bereich:** `src/Client/` (ohne `Hosting/`), `src/Audio/{Abstractions,Headless,Linux,Windows}/`, `src/CalloraVoipSdk/CalloraVoipSdk.csproj`, `src/Directory.Build.props` sowie alle `.csproj`-Dateien der genannten Projekte. Alle Dateien wurden vollständig gelesen.

---

## 1. Überblick & Verantwortung

Das Client-Projekt (`CalloraVoipSdk.Client`) ist die **öffentliche Fassaden-Schicht** über dem Core (`CalloraVoipSdk.Core`, SIP/RTP/STUN/TURN-Runtime). Es besteht aus zwei parallelen Fassaden (ADR-012, „Two-Facade Composition"):

1. **SIP-Fassade** (`VoipClient`/`IVoipClient`, Namespace `CalloraVoipSdk`): Komposition von Line-Management (Registrierung), Call-Management, Medien-Orchestrierung, Audio-Geräten und Convenience-Workflows (Connect/Dial). Die eigentliche SIP/RTP-Logik lebt im Core; der Client verdrahtet die Core-Ports und exponiert Manager-Interfaces.
2. **WebRTC-Fassade** (`WebRtcClient`/`IWebRtcClient`, Namespace `CalloraVoipSdk.WebRtc`): Signalling-neutrale `IPeerConnection`s, die intern ICE, DTLS-SRTP, BUNDLE und RTP/RTCP fahren. Die App besitzt Signalling und Codec („transport-only"). Vier-Ebenen-Design: L1 = PeerConnection + `ConnectAsync`-Happy-Path, L2 = `IPeerConnectionManager` (Peer-Registry), L3 = Plugin-Seam (`IWebRtcClientModule`, MediaTaps, Recorder), DI-Tier via `AddCalloraWebRtc`.

Ergänzend:

- **Manager-Schicht** (`Session`, `Module`, `Device`, `Quality`, `Policy`, `Telemetry`): dünne, interface-basierte Laufzeit-Facetten des `VoipClient` (Read-only-Sichten, Delegation an Core bzw. Gerät).
- **Modul-System**: Zwei parallele Registries (`ModuleRegistry` für SIP, `WebRtcModuleRegistry` für WebRTC), über die separate Pakete Feature-Verträge beisteuern; Playback/Recording sind als eingebaute „Core-Module" über Adapter angebunden.
- **Audio-Backends**: `Audio.Abstractions` (Marker-Interface + geteilte PCM-Helfer), `Audio.Headless` (No-op), `Audio.Linux` (PortAudio/ALSA/Pulse), `Audio.Windows` (NAudio WaveIn/WaveOut). Beide Plattformgeräte unterstützen G.711 (PCMU/PCMA), G.722 und Opus und implementieren Runtime-Controls (Hot-Switch, Mute, Volume, Format).
- **Paketierung**: `CalloraVoipSdk` ist ein Meta-Paket (Client + Audio.Abstractions). Plattform-Audio wird zur Laufzeit per Reflection nachgeladen (`PlatformAudioDeviceFactory` im Core), sodass Windows-/Linux-Pakete optionale Zusatzpakete bleiben. Alle Projekte multitargeten `net8.0;net9.0;net10.0`, Version 4.6.0-preview.1 (Fallback; Release-Version kommt aus dem Git-Tag), SourceLink/snupkg aktiviert (`src/Directory.Build.props`).

---

## 2. Architektur

### 2.1 Builder-/Options-Muster (doppelt gespiegelt)

Beide Fassaden folgen exakt demselben Muster:

| | SIP-Fassade | WebRTC-Fassade |
|---|---|---|
| Mutable Host-Options | `VoipOptions` | `WebRtcOptions` |
| Immutable Runtime-Config | `VoipConfiguration` | `WebRtcConfiguration` |
| Pure-Function-Mapping | `VoipOptionsMapping.ToConfiguration` | `WebRtcOptionsMapping.ToConfiguration` |
| DI-Entry | `AddCalloraVoip(...)` → `CalloraBuilder` | `AddCalloraWebRtc(...)` → `CalloraWebRtcBuilder` |
| Builder-Overrides | `WithAudioDevice<T>`, `WithTelemetrySink<T>`, `WithIce`, `WithTransport`, `AddWebRtc` | `WithVideo`, `WithDtlsCertificate`, `WithLoggerFactory`, `WithStunServer`, `WithTurnServer`, `WithIceServers` |
| Startup-Validierung | `VoipOptionsValidator` (`ValidateOnStart`) | **fehlt** (siehe Befunde) |

Die Builder-Overrides arbeiten ausschließlich über `PostConfigure<TOptions>`, laufen also garantiert nach der Caller-Konfiguration. Die Komposition beider Fassaden geschieht in einer Kette: `services.AddCalloraVoip(...).AddWebRtc(...)` (`CalloraBuilder.AddWebRtc`, `src/Client/Infrastructure/DependencyInjection/VoipSdkBuilder.cs:64`).

Direkte Konstruktion ohne DI ist gleichwertig möglich: `new VoipClient(new VoipConfiguration{...})` bzw. `new WebRtcClient()` (Zero-Config: Loopback-Endpoint, Opus, frische DTLS-Identität pro Peer).

### 2.2 Modul-System

- `IVoipClientModule` / `IWebRtcClientModule`: Marker-Verträge mit `ModuleId` und Default-Interface-Methode `OnAttached(client)` (No-op). Modulpakete definieren eigene Feature-Interfaces obendrauf; die Fassade referenziert nie konkrete Modultypen.
- `ModuleRegistry` / `WebRtcModuleRegistry`: thread-sichere Listen mit `Register`/`Get<T>`/`TryGet<T>`. `OnAttached` läuft **vor** der Aufnahme in die Liste, sodass Konsumenten nie ein halb-initialisiertes Modul auflösen. First-registered-wins bei Mehrfachtreffern. Fehlschlag wirft `ModuleFeatureUnavailableException`.
- DI-Autoregistrierung: Alle als `IVoipClientModule`/`IWebRtcClientModule` registrierten Services werden im Client-Konstruktor eingesammelt — bewusst als **letzter** Konstruktionsschritt, damit `OnAttached` einen vollständig gebauten Client sieht.
- Eingebaute Module: `ModuleAdapters` erzeugt `CorePlaybackModule`/`CoreRecordingModule`, die 1:1 an den Core-`MediaManager` delegieren (`IsAvailable => true`); `WebRtcRecorder` ist gleichzeitig eigenständig nutzbar und als `IWebRtcClientModule` registrierbar.

### 2.3 Beziehung Client → Core (verdrahtete Core-Ports)

Der `VoipClient`-Konstruktor (`src/Client/Application/Facades/VoipClient.cs:137–364`) ist das Kompositionswurzel-Stück. Er löst jeden Port zuerst optional aus dem `IServiceProvider` und fällt sonst auf die Core-Default-Implementierung zurück („resolve-or-default"):

| Core-Port | Default | Zweck |
|---|---|---|
| `ISipTransportFactory` → `ISipTransportRuntime` | `SipTransportFactory` | SIP-Transport (UDP/TCP/TLS/WS/WSS-Listener) |
| `ISipDigestAuthenticator` | `SipDigestAuthentication` | Digest-Auth |
| `ISipTelemetrySink` | `NullSipTelemetrySink` (dekoriert durch `ClientTelemetrySink`) | Events/Metriken/CDRs |
| `ISipRegistrationService` / `ISipCallSignalingService` | `SipRegistrationService` / `SipCallSignalingService` | REGISTER / INVITE-Dialoge (Ownership-Flags `_ownsX` steuern Dispose) |
| `ISdpNegotiator` | `SdpNegotiator` (+ `SipSessionSdpProvider`-Delegates) | SDP Offer/Answer/Hold |
| `ICallIceAgent` | `CallIceAgent` (nur wenn `config.Ice.Enabled`; baut `StunClient`/`StunIceProbe`, optional `TurnClient`/`TurnIceRelayAllocator` bei TURN-Servern) | ICE |
| `IAudioFileCodecRegistry` | `AudioFileCodecRegistry` | WAV/MP3-Codecs für Playback/Recording |
| `IDtlsSrtpHandshaker` / `DtlsCertificate` | `DtlsSrtpHandshaker` / DI-Zertifikat → Config-Zertifikat → ephemeres ECDSA P-256 | DTLS-SRTP (RFC 5763) |
| `ICallMediaSessionFactory` | `RtpCallMediaSessionFactory` (mit Bridge-Transcoding-Codec aus `BridgeAudioFormat`) | RTP-Sessions pro Call |
| `IRtcpPacketCodec` | `RtcpPacketCodec` | RTCP |
| `IAudioDevice` | via `PlatformAudioDeviceFactory.Resolve` (Reflection-Load von `CalloraVoipSdk.Audio.Windows/Linux`, Fallback `SilenceAudioDevice`) | Gerät |
| `IVideoDevice` | **nur DI**, kein Fallback — Video „fails closed" | Video-Codec-Paket der App |

Daraus baut er: `CallManager`, `MediaManager`, `CallMediaOrchestrator` (an `CallStateChanged` gehängt), `PhoneLineManager` (mit Factory, die pro Account einen `SipLineChannel` + `PhoneLine` erzeugt und via `onCallCreated` jede neue Call an den Media-Orchestrator anbindet) und `SdkConvenienceOrchestrator` (Connect/Dial/AttachAudio/AttachVideo).

Die WebRTC-Seite verdrahtet analog pro Peer: `WebRtcPeerConnection` (Core-intern) mit `SdpOfferAnswerNegotiator`, `SdpSessionParser/Serializer`, `DtlsSrtpHandshaker`, `DtlsCertificate`, optional `StunIceProbe` (nur bei konfigurierten ICE-Servern) und `TurnAllocationProbe` (nur bei UDP-TURN).

### 2.4 Audio-Architektur

`IAudioDeviceProvider` (Abstractions) erbt vom Core-Port `IAudioDevice` und ist reiner Marker. Die Plattformgeräte implementieren zusätzlich `IAudioDeviceRuntimeControl` (Core-Port) für Runtime-Steuerung; der `DeviceManager` im Client castet das konfigurierte Gerät darauf und wirft `NotSupportedException`, wenn das Gerät (z. B. `SilenceAudioDevice`, `HeadlessAudioDevice`) keine Runtime-Controls bietet (`VoipClient.cs:680–687`). Geteilte Bausteine: `BoundedPlaybackBuffer` (bounded, drop-oldest, Channel-basiert) und `PcmGain` (In-place-Mute/Volume auf PCM16).

---

## 3. Klassenkatalog

### 3.1 `src/Client/Application/Facades/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `IVoipClient` (interface) | `IVoipClient.cs` | Öffentlicher SDK-Vertrag: Properties für `Calls`, `Lines`, `Media`, alle Manager, Events `IncomingCall`/`CallStateChanged`, Workflows (`ConnectAsync`, `DialAndWaitUntilConnectedAsync`), Audio-/Video-Attach, Audio-Runtime-Shortcuts, `OnIncomingCall`-Convenience. `IDisposable`. Enthält obsoletes `RegisterAndWaitAsync`. |
| `VoipClient` (sealed class) | `VoipClient.cs` | Die Kompositionswurzel der SIP-Fassade (737 Zeilen): baut alle Core-Services (resolve-or-default), Manager, Orchestratoren; interne `StartRuntimeAsync`/`StopRuntimeAsync` für den Hosted-Service (Stop: alle Calls auflegen, alle Lines deregistrieren); idempotentes `Dispose` via `Interlocked.Exchange`. |
| `IncomingCallSubscription` (internal) | `IncomingCallSubscription.cs` | Idempotentes Abmelde-Handle für `OnIncomingCall` (Interlocked-Swap des Handlers). |
| `VoipClientInitializationException` | `VoipClientInitializationException.cs` | `InvalidOperationException`-Ableitung für Transport-Initialisierungsfehler (Socket-/Berechtigungsprobleme). |

### 3.2 `src/Client/Application/Managers/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `IDeviceManager` / `DeviceManager` | `IDeviceManager.cs` / `DeviceManager.cs` | Runtime-Audio-Kontrolle: Geräte-Enumeration, Snapshot, Switch, Volume, Mute, Format. Delegiert an eine `Func<IAudioDeviceRuntimeControl>` und ruft vor jeder Operation die injizierte `throwIfDisposed`-Action des Clients. |
| `IModuleManager` / `ModuleManager` | `IModuleManager.cs` / `ModuleManager.cs` | Verfügbarkeits-Fassade für Playback/Recording; erzeugt die beiden Core-Module via `ModuleAdapters`. |
| `IModuleRegistry` / `ModuleRegistry` | `IModuleRegistry.cs` / `ModuleRegistry.cs` | Thread-sichere Registry für `IVoipClientModule`; `OnAttached` vor Sichtbarkeit; `Get<T>` wirft `ModuleFeatureUnavailableException`. |
| `IPolicyManager` / `PolicyManager` | `IPolicyManager.cs` / `PolicyManager.cs` | Exponiert die globale `SrtpPolicy`; `ResolveEffectiveSrtpPolicy(bool?)` mappt Per-Call-Override auf `Required`/`Disabled`/Default. |
| `IQualityManager` / `QualityManager` | `IQualityManager.cs` / `QualityManager.cs` | Snapshot-Zugriff (`call.QualitySnapshot`) und Event-Subscription auf `QualitySnapshotChanged` mit Dispose-Handle. |
| `QualitySubscription` (internal) | `QualitySubscription.cs` | Idempotentes Dispose-Handle (Interlocked-Swap einer `Action`). |
| `ISessionManager` / `SessionManager` | `ISessionManager.cs` / `SessionManager.cs` | Read-only-Sicht: `ActiveCalls` (vom konkreten `CallManager`), `ActivePlaybacks`/`ActiveRecordings` (von den Modulen). |
| `ITelemetryManager` / `TelemetryManager` | `ITelemetryManager.cs` / `TelemetryManager.cs` | .NET-Events für `SipEventRecord`/`SipMetricRecord`/`SipCdrRecord`, gespeist vom internen Sink. |
| `ClientTelemetrySink` (internal, in `TelemetryManager.cs:33`) | — | Dekorator um den DI-`ISipTelemetrySink`: leitet an das Inner weiter **und** re-published als Action-Events für den `TelemetryManager`. |

### 3.3 `src/Client/Application/Modules/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `IPlaybackModule` | `IPlaybackModule.cs` | Playback-Fassade: `IsAvailable`, `Active`, `StartCallAsync(call, PlaybackRequest)`, `StartMixedBusAsync(bus, ...)` (Konferenz). |
| `IRecordingModule` | `IRecordingModule.cs` | Recording-Fassade: analog mit `RecordingOptions`. |
| `IVoipClientModule` | `IVoipClientModule.cs` | Modul-Marker mit `ModuleId` und Default-`OnAttached(IVoipClient)`. |
| `ModuleFeatureUnavailableException` | `ModuleContracts.cs` | Fehlertyp bei nicht registriertem Feature-Vertrag. |

### 3.4 `src/Client/Application/Workflows/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `ConnectOptions` | `ConnectOptions.cs` | `Timeout` (15 s), `FailFastOnRegistrationFailed` (true); statisches `Default`. |
| `ConnectResult` | `ConnectResult.cs` | Ergebnismodell mit `Status`, `Line`, `FinalLineState`, `Error`, `IsSuccess`; private Ctor + statische Factories `Registered/Timeout/Canceled/Failed`. |
| `ConnectStatus` (enum) | `ConnectStatus.cs` | `Registered`, `Timeout`, `Canceled`, `Failed`. |
| `DialResult` | `DialResult.cs` | Analoges Ergebnismodell für Dial-and-Wait (`Call`, `FinalCallState`). |
| `DialStatus` (enum) | `DialStatus.cs` | `Connected` (auch `OnHold` zählt), `Timeout`, `Canceled`, `Failed`. |
| `DialWaitOptions` | `DialWaitOptions.cs` | `DialOptions?`, `ConnectTimeout` (30 s), `HangupOnTimeout`/`HangupOnCancellation` (beide true). |

### 3.5 `src/Client/Domain/Configuration/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `BridgeAudioFormat` (enum) | `BridgeAudioFormat.cs` | `Passthrough` (roher Wire-Codec) / `Pcmu` (SDK transkodiert transparent zu/von G.711 µ-law, z. B. für AI-Realtime-Bridges; unterstützte Wire-Codecs: Opus, G.711). |
| `SipTransport` (enum) | `SipTransport.cs` | `Udp`/`Tcp`/`Tls`/`Ws`/`Wss`; wählt nur den Default für Outbound-Routing — gehört wird auf allen. |
| `VoipConfiguration` | `VoipConfiguration.cs` | Immutable (init-only) Runtime-Konfiguration: `UserAgent`, `Tls`, `DefaultTransport`, `LoggerFactory`, obsoletes `Services`, `SrtpPolicy` (Default `Optional`), `OfferDtlsSrtp`, `DtlsCertificate` (nur ECDSA P-256), `EnableVideo` (+ ausführliche Doku der Einschränkungen: kein SDES-Video, kein Video-ICE), `PreferredVideoCodecs`, `Ice`, `MaxConcurrentCallsPerLine` (10), `AudioDevice` (Default `SilenceAudioDevice`), `EnableAutomaticAudioDeviceSelection` (true), `PreferredAudioCodecs` (Opus opt-in), `BridgeAudioFormat`, `InboundMediaTimeout` (15 s, NAT-Fallback-Hangup), `HangupHeldCallOnMediaSilence`. |
| `VoipOptions` | `VoipOptions.cs` | Mutable DI-Pendant mit identischen Feldern (ohne `Services`), `Ice` als `IceOptions` (Core). |

### 3.6 `src/Client/Infrastructure/DependencyInjection/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `ServiceCollectionExtensions` (static) | `ServiceCollectionExtensions.cs` | `AddCalloraVoip`: Options + Validator (`ValidateOnStart`), `TryAddSingleton<IVoipClient>` (Factory: Options→Config→`VoipClient(config, sp)`), Singleton-Alias auf `VoipClient`, `CalloraHostedService` via `TryAddEnumerable`. |
| `VoipOptionsMapping` (internal static) | `VoipOptionsMapping.cs` | Pure Funktion `VoipOptions → VoipConfiguration`; jedes Feld wird übertragen. |
| `VoipOptionsValidator` | `VoipOptionsValidator.cs` | Startup-Validierung: UserAgent nicht leer, `MaxConcurrentCallsPerLine >= 0`, `InboundMediaTimeout >= 0` (Zero = Disable-Sentinel, HARD-E9), ICE-Timeouts/-Retries, pro ICE-Server Host/Port/TURN-Credentials. |
| `CalloraBuilder` | `VoipSdkBuilder.cs` | Override-Builder (siehe 2.1); `AddWebRtc` komponiert die WebRTC-Fassade. |
| `CalloraHostedService` | `VoipSdkHostedService.cs` | `IHostedService`: Start → `StartRuntimeAsync`; Stop → `StopRuntimeAsync` (Fehler geloggt, OCE rethrown) und `_client.Dispose()`. Arbeitet nur bei konkretem `VoipClient` mit dem Runtime-Lifecycle. |

### 3.7 `src/Client/Infrastructure/Modules/` und `Properties/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `ModuleAdapters` (internal static) | `ModuleAdapters.cs` | Factory für die beiden Core-Module. |
| `CorePlaybackModule` (internal) | `ModuleAdapters.cs:19` | Delegiert `StartCallAsync`/`StartMixedBusAsync` an `MediaManager.StartCallPlaybackAsync`/`StartConferencePlaybackAsync`. |
| `CoreRecordingModule` (internal) | `ModuleAdapters.cs:32` | Analog für Recording. |
| `AssemblyInfo` | `Properties/AssemblyInfo.cs` | `InternalsVisibleTo("CalloraVoipSdk.Client.Tests")`. |

### 3.8 `src/Client/WebRtc/` (40 Dateien)

**Fassade & Verwaltung**

| Typ | Datei | Beschreibung |
|---|---|---|
| `IWebRtcClient` | `IWebRtcClient.cs` | `CreatePeer()`, `Peers`, `Modules`. Spiegelt `IVoipClient`. |
| `WebRtcClient` (sealed) | `WebRtcClient.cs` | Baut pro Peer: DTLS-Zertifikat (gepinnt oder ephemer P-256), `WebRtcPeerOptions` (Codecs, Video/Simulcast, ICE-Ufrag/Pwd mit `a=ice-options:trickle`), STUN-Probe (nur bei ICE-Servern), TURN-Probe (nur bei UDP-TURN), Core-`WebRtcPeerConnection`, wickelt in `PeerConnection` und trackt im Manager. |
| `WebRtcCodecCatalog` (internal static, `WebRtcClient.cs:153`) | — | Audio-Codec-Namen → `SdpCodecDefinition` (opus PT 111/48k/2ch, PCMU 0, PCMA 8, G722 9); unbekannte Namen werfen. Video läuft über den Core-`VideoCodecCatalog`. |
| `IPeerConnectionManager` / `PeerConnectionManager` (internal) | eigene Dateien | L2-Registry: `Active` (Snapshot-Array unter Lock), `Count`; `Track`/`Untrack` intern. |
| `IWebRtcClientModule` / `IWebRtcModuleRegistry` / `WebRtcModuleRegistry` (internal) | eigene Dateien | L3-Plugin-Seam, strukturell identisch zum SIP-Pendant. |

**PeerConnection**

| Typ | Datei | Beschreibung |
|---|---|---|
| `IPeerConnection` | `IPeerConnection.cs` | Zentrale Abstraktion (spiegelt `ICall`): `State`, `LocalDescription`, `LocalMediaEndPoint`, Events (`ConnectionStateChanged`, `TrackReceived`, `LocalIceCandidateDiscovered`, `DtmfReceived`), `CreateOffer`, `SetRemoteDescriptionAsync`, `AddIceCandidateAsync`, `GatherCandidatesAsync` (muss vor `StartAsync` laufen — teilt den Media-Socket), `StartAsync`, `SendAudioAsync`/`SendVideoFrameAsync` (+ Simulcast-RID-Overload), `SendDtmfAsync` (RFC 4733), `AttachMediaTap`, `GetStats`. `IAsyncDisposable`. |
| `PeerConnection` (internal sealed) | `PeerConnection.cs` | Adapter über der Core-`WebRtcPeerConnection`: mappt internen State-Enum + `Action`-Events auf den öffentlichen Vertrag, projiziert Inbound-Medien via `RemoteTrackSet` aufs W3C-Track-Modell (Materialisierung bei `SetRemoteDescriptionAsync`, Fallback beim ersten Frame), fächert beide Richtungen an `MediaTapSet` aus, berechnet in `GetStats` Bitraten (`BitrateMeter`), FPS (`RateMeter`), RTCP-Qualität (RTT/Loss/Jitter, „null statt 0"-Philosophie) und Per-Stream-Breakdown; `DisposeAsync` hängt Events ab und untrackt auch bei Fehlern (`finally`). |
| `PeerConnectionState` (enum) | `PeerConnectionState.cs` | W3C `RTCPeerConnectionState`: `New/Connecting/Connected/Disconnected/Failed/Closed`. |
| `WebRtcRole` (enum) | `WebRtcRole.cs` | `Offerer`/`Answerer` für den SDK-getriebenen Handshake. |

**Signalling**

| Typ | Datei | Beschreibung |
|---|---|---|
| `IWebRtcSignaling` | `IWebRtcSignaling.cs` | App-eigener SDP-Kanal: `SendDescriptionAsync`/`ReceiveDescriptionAsync`. |
| `IWebRtcTrickleSignaling` | `IWebRtcTrickleSignaling.cs` | Erweiterung um Out-of-band-Trickle (RFC 8838/8840): `SendCandidateAsync`, `ReceiveCandidateAsync` (null = end-of-candidates), `SendEndOfCandidatesAsync`. Kandidaten als RFC-8829-`candidate:`-Zeilen (browserkompatibel). |
| `WebRtcPeerConnectionExtensions` (static) | `WebRtcPeerConnectionExtensions.cs` | `ConnectAsync(peer, signalling, role, ct)`: der Happy-Path (Details unter 4.2). |
| `WebRtcConnectException` | `WebRtcConnectException.cs` | Fehlertyp von `ConnectAsync` (Failed/Closed während Negotiation, Signalling-Fehler beim Trickle). |

**Tracks & Frames**

| Typ | Datei | Beschreibung |
|---|---|---|
| `RemoteTrack` | `RemoteTrack.cs` | W3C-artiger Remote-Track (`Kind`, `StreamId`/`TrackId` aus `a=msid`, RFC 8830), Event `FrameReceived`. |
| `RemoteTrackSet` (internal) | `RemoteTrackSet.cs` | Genau ein Track pro Kind, Callback exakt einmal (Lock-geschützt); Vorbedingung: serielle Frame-Zustellung durch die Single-Receive-Loop des Peers. |
| `TrackKind` (enum) / `MediaDirection` (enum) | eigene Dateien | Audio/Video bzw. Inbound/Outbound. |
| `EncodedFrame` (readonly struct) | `EncodedFrame.cs` | Payload (nur während des Callbacks gültig), `RtpTimestamp?` (Audio: noch null), `IsKeyFrame`, `PresentationTimeUsec?` (bis RTCP-SR-Mapping: null). |
| `DtmfTone` (readonly record struct) | `DtmfTone.cs` | `ToneCode` (0–15), `DurationMs`. |

**Taps & Recording**

| Typ | Datei | Beschreibung |
|---|---|---|
| `IMediaTap` | `IMediaTap.cs` | L3-Observer für encodierte Medien beider Richtungen (`OnAudio`, `OnVideo` mit `rtpTimestamp?`, `isKeyFrame`, `rid?`); Vertrag: schnell, nicht werfen (wird isoliert). |
| `MediaTapSet` (internal) | `MediaTapSet.cs` | Copy-on-write-Array (`volatile`), allokationsfrei auf dem Hotpath, werfende Taps werden geloggt und isoliert. |
| `MediaTapHandle` (internal) | `MediaTapHandle.cs` | Idempotentes Detach-Handle. |
| `IEncodedMediaSink` | `IEncodedMediaSink.cs` | Persistenz-Ziel des Recorders: `Write(in RecordedFrame)` (Hotpath, nicht werfen), `CompleteAsync` (genau einmal beim Stop). Container-Muxing (Ogg/IVF/MP4) ist Sink-Sache. |
| `RecordedFrame` (readonly struct) | `RecordedFrame.cs` | Frame + `Kind`, `Direction`, `RtpTimestamp?`, `IsKeyFrame`, `Rid?`. |
| `RecordingTap` (internal) | `RecordingTap.cs` | `IMediaTap` → Sink-Mapping + Interlocked-Frame-Zähler. |
| `IWebRtcRecorder` / `WebRtcRecorder` | eigene Dateien | L3-Modul (`ModuleId "callora.webrtc.recorder"`): `Start(peer, sink)` hängt `RecordingTap` an und liefert `WebRtcRecording`. |
| `IWebRtcRecording` / `WebRtcRecording` (internal) | eigene Dateien | Laufende Aufnahme: `FrameCount`, idempotentes `StopAsync` (erst Tap detachen, dann Sink completen), `DisposeAsync` → `StopAsync`. |

**Stats & Konfiguration**

| Typ | Datei | Beschreibung |
|---|---|---|
| `WebRtcStats` | `WebRtcStats.cs` | `getStats`-Snapshot: Transport-Zähler (inkl. `SuppressedSends` = fail-closed vor SRTP-Keys, `DroppedDatagrams`), abgeleitete Bitraten, RTCP-Qualität (`PacketLoss`, `JitterMs`, `RoundTripTimeMs` als Worst-of-Aggregat), `MediaStreams` (Per-Stream), Video-Metriken (teils null bis „later slice"), ICE-Label + Selected-Candidates, `AvailableOutgoingBitrateBps` (null bis transport-cc). |
| `WebRtcMediaStreamStats` | `WebRtcMediaStreamStats.cs` | Per-Stream-Eintrag: `Mid?`, `Ssrc`, `Kind` ("audio"/"video"/"unknown"), Outbound-RTT/-Loss vs. lokaler Inbound-Jitter. |
| `BitrateMeter` / `RateMeter` (internal) | eigene Dateien | Delta-basierte Raten aus kumulativen Zählern; erste Probe primt nur (null); nicht thread-sicher (Owner serialisiert via `_statsSync`). |
| `WebRtcConfiguration` | `WebRtcConfiguration.cs` | Immutable: `LocalEndPoint` (Default Loopback:0), `AudioCodecs` (["opus"]), `EnableVideo`, `VideoCodecs` (["H264"]), `SimulcastLayers` (RFC 8853), `IceServers`, `DtlsCertificate?`, `LoggerFactory?`. |
| `WebRtcOptions` | `WebRtcOptions.cs` | Mutable DI-Pendant (eigenständige Options-Fläche, nicht in `VoipOptions` genestet). |
| `WebRtcOptionsMapping` (internal static) | `WebRtcOptionsMapping.cs` | Pure Options→Config-Projektion. |
| `WebRtcServiceCollectionExtensions` / `CalloraWebRtcBuilder` | eigene Dateien | DI-Entry + Override-Builder (siehe 2.1). |

### 3.9 `src/Audio/`

| Typ | Datei | Beschreibung |
|---|---|---|
| `IAudioDeviceProvider` | `Abstractions/Domain/Devices/IAudioDeviceProvider.cs` | Marker: `IAudioDevice` aus dem Core. |
| `BoundedPlaybackBuffer` | `Abstractions/Processing/BoundedPlaybackBuffer.cs` | Bounded Channel (SingleReader, DropOldest) für dekodierte PCM-Frames; `Depth`, `DroppedFrames` (Drop-Callback zählt), `Enqueue`/`TryDequeue`/`Clear`. Begründung HARD-F4 (Latenz-/Speicher-Begrenzung). |
| `PcmGain` (static) | `Abstractions/Processing/PcmGain.cs` | In-place Mute/Volume auf PCM16-LE: Mute/≤0 → Nullen, Unity (±0.0001) → unverändert, sonst Skalieren+Clampen. HARD-F1 (keine Hotpath-Allokation). |
| `HeadlessAudioDevice` | `Headless/Infrastructure/HeadlessAudioDevice.cs` | No-op-Provider für Server/Tests (validiert nur Argumente). |
| `AudioDeviceOptions` (Linux) | `Linux/Infrastructure/AudioDeviceOptions.cs` | `InputDeviceIndex`/`OutputDeviceIndex` (-1 = Default), `SampleRate` (8000), `FramesPerBuffer` (160 — **wird nie gelesen**, s. Befunde). |
| `LinuxAudioDevice` | `Linux/Infrastructure/LinuxAudioDevice.cs` | PortAudio-Gerät (ALSA/Pulse), 919 Zeilen: Callback-basiertes Capture/Playback, `BoundedPlaybackBuffer` (50×20 ms), Codec-Auflösung über PayloadType/Name/Map, G.722 via NAudio (gecachte stateless Codec-Instanzen, getrennte Encode/Decode-States), Opus via Core-`OpusDeviceCodec`, **adaptiver Outbound-Codec** (`TryAdaptOutboundCodec` folgt dem Inbound-Codec), naive Sample-Rate-Konvertierung, Runtime-Controls mit Stream-Rebuild, Snapshot inkl. Playback-Queue-Metriken. Privates Enum `ActiveCodec` (Pcmu/Pcma/G722/Opus). |
| `LinuxG711Codec` (internal static) | `Linux/Infrastructure/LinuxG711Codec.cs` | Eigene µ-law/A-law-Implementierung (Tabellen-basiertes µ-law-Encode), PT 0 = µ-law, 8 = A-law. |
| `AudioDeviceOptions` (Windows) | `Windows/Infrastructure/AudioDeviceOptions.cs` | `InputDeviceNumber`/`OutputDeviceNumber`, `SampleRate`, `BitsPerSample` (16), `Channels` (1). |
| `WindowsAudioDevice` | `Windows/Infrastructure/WindowsAudioDevice.cs` | NAudio `WaveInEvent`/`WaveOutEvent` + `BufferedWaveProvider` (DiscardOnBufferOverflow), G.711 via NAudio-Encoder, G.722/Opus wie Linux, Output-Mute zusätzlich über `waveOut.Volume`, Geräte-Enumeration teils über `WaveInterop` (WinMM P/Invoke). **Kein** adaptiver Outbound-Codec, **keine** Queue-Metriken im Snapshot. |

### 3.10 Paketierung

- `src/CalloraVoipSdk/CalloraVoipSdk.csproj`: Meta-Paket, referenziert nur `Client` + `Audio.Abstractions`; „Meta package for CalloraVoipSdk runtime facade and shared abstractions."
- `src/Client/CalloraVoipSdk.Client.csproj`: referenziert Core; MS.Extensions (DI.Abstractions, Hosting.Abstractions, Logging.Abstractions, Options) je 8.0.x; `GenerateDocumentationFile` mit NoWarn 1591.
- Audio-Projekte: Abstractions → Core; Headless → Abstractions + Core; Linux → NAudio.Core 2.3.0 + PortAudioSharp2 1.0.6; Windows → NAudio 2.3.0 (Vollpaket).
- `src/Directory.Build.props`: Version 4.6.0(-preview.1-Fallback), Autoren/Repo-Metadaten (Bechstein Digital), README/LICENSE gepackt, Analyzer `8.0-recommended` mit umfangreicher `NoWarn`-Liste, `ContinuousIntegrationBuild` bei CI, per-Projekt-Descriptions für Core/Audio.Windows/Audio.Linux.

---

## 4. Zentrale Abläufe

### 4.1 SDK-Initialisierung via DI bis zum ersten Call

1. **Registrierung**: `services.AddCalloraVoip(o => {...})` → `VoipOptions` + Validator (`ValidateOnStart` — der Host startet nicht mit invaliden Optionen), lazy Singleton-Factory für `IVoipClient`, `CalloraHostedService`.
2. **Konstruktion** (beim ersten Resolve): `VoipOptionsMapping.ToConfiguration` (pure), dann der `VoipClient`-Konstruktor: LoggerFactory → AudioDevice (DI-Gerät gewinnt, wenn Config auf `SilenceAudioDevice` steht; sonst `PlatformAudioDeviceFactory` mit Reflection-Load der Plattformpakete) → SIP-Transport (Fehler → `VoipClientInitializationException`) → Auth/Telemetry/Registration/Signaling (mit Ownership-Flags) → SDP-Negotiator + Provider-Delegates → optional ICE-Agent (STUN-Probe, TURN-Allocator nur bei TURN-Servern) → `CallManager`/`MediaManager`/`ModuleManager`/`SessionManager`/`DeviceManager`/`QualityManager`/`PolicyManager` → DTLS-Identität (DI > Config-Zertifikat > ephemer P-256) → `RtpCallMediaSessionFactory` (inkl. Bridge-Transcoding) → `CallMediaOrchestrator` (abonniert `CallStateChanged`) → `PhoneLineManager` mit Line-Factory (jeder neue Call wird via `onCallCreated` an den Media-Orchestrator attached) → `SdkConvenienceOrchestrator` → zuletzt Modul-Registrierung (bei Fehler: `Dispose()` + rethrow).
3. **Host-Start**: `CalloraHostedService.StartAsync` → `StartRuntimeAsync` (setzt nur das Flag und loggt; die Runtime ist passiv-lazy).
4. **Erster Call**: `ConnectAsync(account)` → `SdkConvenienceOrchestrator.RegisterAndWaitAsync` (Timeout 15 s, Fail-fast bei `RegistrationFailed`) → `ConnectResult`-Mapping per switch. Danach `DialAndWaitUntilConnectedAsync(line, uri)` → Orchestrator (Timeout 30 s, optionaler Auto-Hangup bei Timeout/Cancel) → `DialResult`. Audio anbinden: `AttachDefaultAudioAsync(call)` (Receiver+Sender+Gerät, Auto-Detach bei Termination, verdrängt eine bestehende Default-Route auf anderer Call).
5. **Host-Stop**: `StopRuntimeAsync` legt alle nicht-terminierten Calls auf (Fehler geloggt, OCE propagiert) und deregistriert alle Lines; anschließend `Dispose` (Orchestratoren → Lines → Signaling/Registration nur bei Ownership → Transport → Audio-Gerät nur bei Ownership).

### 4.2 WebRTC-Peer-Verbindungsaufbau inkl. Signaling-Abstraktion und Trickle-ICE

`WebRtcPeerConnectionExtensions.ConnectAsync` (`src/Client/WebRtc/WebRtcPeerConnectionExtensions.cs`):

1. **Vorab**: `TaskCompletionSource` („established") wird **vor** jedem Handshake-Schritt gearmed; Zustandsübergänge (Connected → Result, Failed/Closed → `WebRtcConnectException`) können so nicht verpasst werden.
2. **Offer/Answer** (RFC 8829) über den App-Kanal: Offerer: `CreateOffer` → `SendDescriptionAsync` → `ReceiveDescriptionAsync` → `SetRemoteDescriptionAsync(answer)`. Answerer: umgekehrt; `SetRemoteDescriptionAsync(offer)` liefert die lokale Answer zurück. Der Host-Kandidat reitet bereits im SDP.
3. **Trickle** (nur wenn das Signalling `IWebRtcTrickleSignaling` implementiert): parallel startet `PumpRemoteCandidatesAsync` (wendet Remote-Kandidaten an, bis null = end-of-candidates oder Cancel; Kanalfehler → `TrySetException` auf „established", was eine bereits etablierte Verbindung nicht mehr maskieren kann). Lokal: `LocalIceCandidateDiscovered` abonnieren → `GatherCandidatesAsync` (srflx via STUN, Relay via TURN-Probe; **muss vor `StartAsync`** laufen, da der Media-Socket geteilt wird) → gepufferte Kandidaten senden → `SendEndOfCandidatesAsync`.
4. **Start & Warten**: `StartAsync` (ICE-Konnektivität, DTLS-Handshake, Receive-Loop), dann Warten auf „established" mit Cancel-Registration.
5. **Aufräumen** (`finally`): Events abhängen, verlinkten CTS canceln, Pump awaiten (beobachtet Cancel intern, wirft nie hinaus).

Besonderheiten: `SetRemoteDescriptionAsync` materialisiert die Remote-Tracks sofort (W3C-`ontrack`-Semantik), sodass `TrackReceived`-Handler vor dem ersten Frame subscriben können; die Bundle-Verbindung nutzt Single-Candidate-Selektion (kein voller ICE-Checklist-FSM) — der ICE-State in `GetStats` ist ein abgeleitetes Label.

### 4.3 Recording-Pipeline (Taps → Recorder → Datei?)

WebRTC-seitig: `PeerConnection` ruft auf jedem Send-/Receive-Pfad `MediaTapSet.Audio/Video` **vor bzw. neben** dem eigentlichen Transport → `WebRtcRecorder.Start(peer, sink)` hängt einen `RecordingTap` als `IMediaTap` an → jeder Frame wird als `RecordedFrame` (Kind, Direction, RTP-TS, KeyFrame, RID) in den `IEncodedMediaSink` geschrieben (synchron, Hotpath, fault-isoliert im `MediaTapSet`) → `WebRtcRecording.StopAsync` detacht zuerst den Tap (danach kommt garantiert kein Frame mehr) und ruft dann `sink.CompleteAsync` genau einmal. **Es gibt bewusst keinen Datei-Writer im Client**: der Sink entscheidet über Persistenz (Container-Muxing wie Opus→Ogg ist explizit Sink-Implementierung). SIP-seitig läuft Recording getrennt über `IRecordingModule` → Core-`MediaManager.StartCallRecordingAsync` (dort existiert die WAV/MP3-Datei-Pipeline des Core).

### 4.4 Options-Validierung/Mapping

- `VoipOptionsValidator` läuft dank `ValidateOnStart` beim Host-Start (fail-fast); geprüft werden UserAgent, Call-Limit, `InboundMediaTimeout` (Zero-Sentinel-Semantik), ICE-Timeout/Retries und jeder ICE-Server (Host, Port 1–65535, TURN-Credentials).
- Beide Mappings (`VoipOptionsMapping`, `WebRtcOptionsMapping`) sind pure statische Funktionen mit vollständigem Feld-Carry-over — dokumentiert als bewusst DI-frei testbar. LoggerFactory-Auflösung: Options-Override > Container-Factory > Null-Logger.

---

## 5. Threading- und Fehlerbehandlungsmodell

**Threading:**

- **Event-Kontrakt**: `IncomingCall`/`CallStateChanged` feuern auf dem SIP-Signaling-Thread; Handler dürfen weder blockieren noch werfen (dokumentiert in `VoipClient.cs:117–127`). `OnIncomingCall` entschärft das für Async-Handler: fire-and-forget mit vollständigem Catch (OCE → Debug-Log, sonst Warning).
- **Locks + Interlocked**: Registries (`ModuleRegistry`, `WebRtcModuleRegistry`, `PeerConnectionManager`, `RemoteTrackSet`) nutzen klassische Monitor-Locks; Dispose-/Stop-Pfad-Idempotenz durchgängig via `Interlocked.Exchange` (`VoipClient._disposed`/`_runtimeStarted`, `IncomingCallSubscription`, `QualitySubscription`, `MediaTapHandle`, `WebRtcRecording._stopped`).
- **Hotpath-Design**: `MediaTapSet` ist copy-on-write (`volatile IMediaTap[]`), Dispatch lockfrei; `BitrateMeter`/`RateMeter` bewusst nicht thread-sicher, vom Owner über `_statsSync` serialisiert; `RemoteTrackSet` verlässt sich auf die dokumentierte serielle Zustellung der Peer-Receive-Loop.
- **Audio-Geräte**: ein `_sync`-Monitor pro Gerät; die Echtzeit-Callbacks (PortAudio-Callback bzw. NAudio-Events) nehmen den Lock nur für kurze Snapshot-Reads der Steuerfelder und arbeiten dann lock-frei; RX-Pfad (netz-getaktet) und Playback-Callback (hardware-getaktet) sind über den bounded Drop-oldest-Buffer entkoppelt (Linux) bzw. `BufferedWaveProvider` (Windows).

**Fehlerbehandlung:**

- **Fail-fast bei Konstruktion**: Transportfehler werden gezielt in `VoipClientInitializationException` übersetzt (`IsTransportInitializationFailure`: `SocketException`/`UnauthorizedAccessException`); Modul-`OnAttached`-Fehler im Konstruktor lösen `Dispose()` der bereits gebauten Ressourcen aus und rethrown das Original.
- **Fault-Isolation auf Medienpfaden**: werfende Taps/Sinks werden gecatcht, geloggt und isoliert (`MediaTapSet.cs:40–58`) — der Medienfluss bricht nie.
- **Graceful Shutdown**: `StopRuntimeAsync` und `CalloraHostedService.StopAsync` loggen Einzel-Fehler als Warning, propagieren aber `OperationCanceledException` (Cancellation gewinnt immer).
- **Ergebnisobjekte statt Exceptions** in den Workflows: `ConnectResult`/`DialResult` transportieren Timeout/Canceled/Failed inkl. letztem State und gefangener Exception; nur `ConnectAsync` (WebRTC) wirft (`WebRtcConnectException`), dort per TCS-Race so gebaut, dass ein später Signalling-Fehler eine bereits stehende Verbindung nicht mehr „failen" kann.
- **„null statt 0"**: Stats melden konsequent `null` für noch nicht gemessene Werte — nie fabrizierte Nullwerte.
- **Fail-closed**: Video ohne registriertes `IVideoDevice` schlägt fehl statt still zu degradieren; SRTP-Sends vor Key-Installation werden unterdrückt und gezählt (`SuppressedSends`).

---

## 6. Qualitätsbefunde

### Stärken

- **Konsequente Spiegel-Architektur** beider Fassaden (Options→Config→Client→Builder→Module) — sehr gut lern- und wartbar; ADR-Referenzen (ADR-012) und interne Härtungs-IDs (HARD-C4/E5/E7/E9/F1/F4) direkt im Code dokumentiert.
- **Vorbildliche XML-Dokumentation** mit RFC-Zitaten (RFC 3264/3550/4733/5763/8122/8445/8656/8829/8838/8840/8853/8830) und expliziten Verträgen (Threading, Payload-Lebensdauer, „later slice"-Markierungen).
- Saubere **Ownership-Semantik** (`_ownsRegistrationService`, `_ownsAudioDevice` …) verhindert Doppel-Dispose DI-injizierter Services.
- Durchdachte Nebenläufigkeit: idempotente Dispose-Handles, copy-on-write-Hotpaths, bounded Buffer mit Drop-Metrik, TCS-Arming vor Handshake.
- Testfreundlichkeit: pure Mapping-Funktionen, extrahierte Kleinklassen (`MediaTapSet`, `RecordingTap`, `RemoteTrackSet`, `BitrateMeter`), `InternalsVisibleTo`, `TryAdd*`-DI (Consumer-Overrides möglich).

### Potenzielle Bugs / Risiken

1. **`VoipClient`-Konstruktor leakt Ressourcen bei mittigem Fehlschlag** (`src/Client/Application/Facades/VoipClient.cs:166–343`): Nur Modul-Registrierungsfehler (Z. 349–362) räumen via `Dispose()` auf. Wirft z. B. `DtlsCertificate.FromX509` (Z. 284–287), der ICE-Aufbau oder die Line-Manager-Konstruktion, bleiben `_transportRuntime`, Registration-/Signaling-Service und ggf. das eigene Audio-Gerät undisposed (offene Sockets/Listener).
2. **Nicht thread-sichere Event-Accessors in `PeerConnection`** (`src/Client/WebRtc/PeerConnection.cs:49–71`): Die Custom-Accessors machen `_field += value` ohne Synchronisierung — anders als Compiler-generierte field-like Events (CAS-Loop) können konkurrierende Subscribes/Unsubscribes Handler verlieren.
3. **`DateTime.UtcNow.Ticks` als Raten-Uhr** (`PeerConnection.cs:105`): nicht monoton; NTP-Sprünge verfälschen `OutgoingBitrateBps`/`FramesPerSecond` (Rückwärtssprung wird nur bei ≤0-Delta abgefangen). `Stopwatch`/`Environment.TickCount64` wäre korrekt.
4. **Totes Konfigurationsfeld**: `AudioDeviceOptions.FramesPerBuffer` (Linux, `src/Audio/Linux/Infrastructure/AudioDeviceOptions.cs:21`) wird vom `LinuxAudioDevice`-Konstruktor (Z. 74–84) nie gelesen — die Puffergröße kommt immer aus `ComputeFramesPerBuffer` (Z. 632). Konsument-Konfiguration wirkt still nicht.
5. **Plattform-Verhaltensdivergenz Outbound-Codec**: Linux adaptiert den Outbound-Codec/PayloadType an den zuletzt empfangenen Inbound-Codec (`LinuxAudioDevice.cs:754–767` `TryAdaptOutboundCodec`), Windows nicht. Zudem kann die Linux-Adaption bei einem Peer, der einen nicht ausgehandelten PT sendet, dazu führen, dass das SDK selbst einen nicht ausgehandelten PT zurücksendet.
6. **Windows: Playback-Metriken fehlen und Drop-Politik ist invertiert**: `GetRuntimeSnapshot` (`WindowsAudioDevice.cs:189–208`) liefert keine `playbackQueueDepth`/`droppedPlaybackFrames` (Linux ja); `BufferedWaveProvider.DiscardOnBufferOverflow` (Z. 488) verwirft bei Überlauf die **neuesten** Samples — das Gegenteil der in `BoundedPlaybackBuffer` dokumentierten „drop-oldest = jitter-buffer-korrekt"-Politik (HARD-F4).
7. **Windows: `SetOutputVolume` umgeht den Mute** (`WindowsAudioDevice.cs:263–275`): setzt `waveOut.Volume = Clamp(volume)` auch bei `_outputMuted == true` (im Gegensatz zu `SetOutputMuted`, Z. 289–299). Hörbar bleibt es still (PcmGain nullt die Samples), aber die Hardware-Lautstärke ist inkonsistent zum Mute-Zustand.
8. **Fire-and-forget-Sends auf dem Capture-Pfad** (`LinuxAudioDevice.cs:504,519`; `WindowsAudioDevice.cs:449,464`): `_ = localSender.SendAsync(...)` — Exceptions sind unbeobachtet, keine Backpressure; ein dauerhaft fehlschlagender Sender bleibt unsichtbar.
9. **Hotpath-Allokation im Linux-Playback-Callback** (`LinuxAudioDevice.cs:449`): pro Silence-Callback `new byte[bytes]` — im Echtzeit-Audio-Callback (widerspricht dem eigenen HARD-F1-Ziel). Zudem wird ein Frame, der kürzer als der Callback-Puffer ist, komplett verworfen und durch Stille ersetzt (Z. 443–450).
10. **PortAudio-Refcount-Ungleichgewicht**: `GetAvailableInputDevices`/`GetAvailableOutputDevices`/`GetInputDevices`/`GetOutputDevices` rufen `PortAudio.Initialize()` (`LinuxAudioDevice.cs:160,185,355,373`) ohne je `Terminate` — nur `Dispose` terminiert genau einmal (Z. 400); je nach PortAudio-Refcounting bleibt die Library initialisiert.
11. **`ConnectAsync`-Hänger bei nicht-kooperativem Trickle-Kanal** (`WebRtcPeerConnectionExtensions.cs:108–110`): das `finally` awaitet den Kandidaten-Pump; honoriert eine App-Implementierung von `ReceiveCandidateAsync` das CancellationToken nicht, hängt `ConnectAsync` unbegrenzt.
12. **Race in `VoipClient.Dispose` vs. laufende Operationen**: `ThrowIfDisposed` prüft nur zu Methodenbeginn (`VoipClient.cs:731–735`); ein paralleler `Dispose` während `ConnectAsync`/`DialAndWait...` reißt Orchestratoren unter der laufenden Operation weg (übliches, aber undokumentiertes Verhalten).
13. **`WebRtcClient` ohne Teardown**: `IWebRtcClient` ist nicht `IDisposable`; der `PeerConnectionManager` hält starke Referenzen bis zum Peer-`DisposeAsync`. Vergisst der Caller das Disposen, gibt es keine Client-seitige Aufräum-API. Auch die Modul-Registrierung im Konstruktor (`WebRtcClient.cs:46–52`) hat — anders als `VoipClient` — keinen Cleanup-Pfad bei werfendem `OnAttached` (hier allerdings harmlos, da noch keine unmanaged Ressourcen existieren).

### API-Design-Auffälligkeiten

14. **Inkonsistente Argument-Validierung**: `ConnectAsync` prüft `account` auf null (`VoipClient.cs:448`), `DialAndWaitUntilConnectedAsync` prüft weder `line` noch `targetUri` (Z. 476–483).
15. **Fehlender `WebRtcOptions`-Validator**: `AddCalloraWebRtc` (`WebRtcServiceCollectionExtensions.cs:25`) registriert weder `ValidateOnStart` noch einen `IValidateOptions` — asymmetrisch zur SIP-Seite (z. B. würden invalide ICE-Server erst bei `CreatePeer` auffallen).
16. **Event-`sender`-Passthrough**: Bei den weitergereichten Events (`VoipClient.cs:260,335`) ist `sender` der innere Manager (`CallManager`/`PhoneLineManager`), nicht der `VoipClient` — für Konsumenten, die `sender` casten, überraschend.
17. **Versionierungs-Widerspruch in Obsolete-Botschaften**: `RegisterAndWaitAsync` und `VoipConfiguration.Services` sagen „will be removed after v1.0" (`IVoipClient.cs:60`, `VoipConfiguration.cs:37`), während `Directory.Build.props:6` bereits `4.6.0` trägt — die Entfernungszusage ist veraltet.
18. **Doku-Drift in `WebRtcStats`**: `FramesPerSecond`/`KeyFrames` sind als „until video metrics are wired → null" dokumentiert (`WebRtcStats.cs:79–88`), werden aber in `PeerConnection.GetStats` bereits befüllt (Z. 108, 143).
19. **Duplizierter Code zwischen den Plattformgeräten**: `ConvertPcmSampleRate` (Nearest-Neighbor ohne Filter → Aliasing beim Downsampling), `ResolveActiveCodec`, `MapCodecNameToActiveCodec`, G.722-Encode/Decode sind praktisch identisch dupliziert (`LinuxAudioDevice.cs:769–904` vs. `WindowsAudioDevice.cs:632–797`) — genau die Extraktion, die für `PcmGain` bereits vollzogen wurde, steht hier noch aus.
20. **Windows-Optionen versprechen mehr als das Gerät kann**: `AudioDeviceOptions.BitsPerSample`/`Channels` sind frei setzbar (`Windows/Infrastructure/AudioDeviceOptions.cs:24–27`), aber die G.711/G.722-Pfade nehmen Mono/16-bit an und `UpdateFormat` lehnt alles andere ab — der Konstruktor validiert nicht.
21. **Kein `[SupportedOSPlatform]`**: `WindowsAudioDevice` nutzt WinMM (`WaveInterop`, Z. 174–178, 352–356, 598) ohne Plattform-Annotation/`TargetPlatform` — auf Nicht-Windows erst Laufzeitfehler statt Analyzer-Warnung.
22. **Breite globale `NoWarn`-Liste** (`Directory.Build.props:27`): unterdrückt u. a. CA5350/CA5351 (schwache Kryptographie) und CA2016 (CancellationToken-Weiterleitung) solution-weit — für ein Security-nahes SDK ein stumpfes Instrument.

### TODOs / bekannte Lücken

Es gibt **keine TODO/FIXME/HACK-Marker** im analysierten Bereich; offene Punkte sind stattdessen sauber als „later slice"/ADR-012-Deferred im Doc-Kommentar markiert: Inbound-RID-Demux (`PeerConnection.cs:274`), Audio-RTP-Timestamps und `PresentationTimeUsec` via RTCP-SR (`EncodedFrame.cs:21–34`), `FramesDropped`/NACK/PLI/FIR/available-bitrate (`PeerConnection.cs:144–146`), redundante Kandidaten-Pruning RFC 8445 §5.4 (`IPeerConnection.cs:71`), TCP/TLS-TURN (`CalloraWebRtcBuilder.cs:73`), Video-ICE und SDES-Video (`VoipConfiguration.cs:61–75`).

---

**Gesamtbild**: Eine überdurchschnittlich sorgfältig dokumentierte, konsistent gespiegelte Fassaden-Architektur mit sauberem Options-/Builder-/Modul-Muster und robusten Nebenläufigkeits-Primitiven. Die substanziellen Risiken konzentrieren sich auf (a) unvollständiges Konstruktions-Rollback im `VoipClient`, (b) Plattform-Paritätslücken und Code-Duplikation der Audio-Geräte und (c) einzelne Hotpath-/Uhr-Detailfehler in der WebRTC-Statistik.



---

# Teil 7 — Tests, Performance, Beispiele, Build/CI


## 1. Test-Strategie gesamt

Das Repo verfolgt eine explizit dokumentierte, mehrstufige Teststrategie mit einem **Ebenenmodell L0–L4**, definiert in `docs/audit/2026-07-21-interop-soak-audit-design.md:61-71`:

| Ebene | Gegenstand | Testort |
|---|---|---|
| **L4 Facade** | `VoipClient`/`IVoipClient` E2E, Fremd-Stack | InteropTests (Asterisk/Docker) |
| **L3 Signaling/Call** | `SipCoreCallChannel`, `ISipCallSession` | Core.IntegrationTests, SoakTests (`SipRegisterLoopHarness`) |
| **L2 Media** | `RtpCallMediaSession` (RTP/RTCP-Loopback) | Core.IntegrationTests, SoakTests (`RtpMediaLoopback`) |
| **L1 Security** | SRTP/SRTCP/DTLS | Core.IntegrationTests |
| **L0 Wire** | SIP-/SDP-/STUN-Framer, malformed input | Core.IntegrationTests (z. B. `SipWireRobustnessTests`, `StunMessageCodecWireBoundsTests`) |

Pyramidenform de facto: eine sehr breite Basis aus komponentennahen Integrationstests (Core.IntegrationTests: **231 Dateien, ~34.800 Zeilen**), darüber Facade-/DI-Tests (Client.Tests), quer dazu Architektur-Gates, Soak-/Leak-Tests und ein (noch schmaler) echter Interop-Layer. Kategorisierung über xUnit-Traits: `SoakShort` (PR-CI-Smoke), `SoakLong` (nightly), `Interop` (Docker).

**Audit-Register vorhanden**: `docs/audit/INTEROP_SOAK_AUDIT.md` ist ein lebendes Fehlerregister (F001–F004) mit Typ, Evidenz, Root-Cause, Datei:Zeile, Fix-Vorschlag, Schweregrad — bemerkenswert diszipliniert („nur Dokumentation, kein autonomes Fixen"; adversarial verifizierte Kausalkette zu F002). Bekannte offene Befunde:
- **F002 (Media-Defekt, offen)**: Late-Drops werden fälschlich als `PacketsUnrecoverableLoss` gezählt (`RtpCallMediaSession.EmitConcealmentFramesIfNeeded`); der zugehörige Soak-Test ist mit `Skip` markiert (`tests/CalloraVoipSdk.SoakTests/Soak/MediaQualityDriftSoakTests.cs:76`).
- **F003**: keine `ITimeProvider`-Abstraktion im Signaling → kein echtes Zeit-Raffen für Langzeit-Soaks.
- **F004**: RTT auf L2 ist statischer Hint, keine Live-RTCP-Messung — es existiert sogar ein „Schicht-Grenzen-Wächter"-Test, der fehlschlägt, falls sich das ändert (`RoundTripTime_IsStaticHint_NotLiveRtcpMeasurement_F004`).

## 2. Testprojekte im Einzelnen

### 2.1 CalloraVoipSdk.ArchitectureTests (net10.0, 2 Dateien)

**Zweck**: Mechanische Gates für `ENGINEERING_RULES.md`, quelltextbasiert (Regex über den Dateibaum, kein Roslyn). **Harness**: `SourceScan.cs` — Repo-Root-Suche, Datei-Enumeration ohne obj/bin, `DeclaredNamespace`, `LayerSegmentViolation` und der zentrale Mechanismus `AssertMatchesBaseline`: **Baselines dürfen nur schrumpfen** — neue Verstöße UND veraltete Baseline-Einträge schlagen fehl (`SourceScan.cs:97-122`). Geprüfte Regeln (`EngineeringRulesTests.cs`):
- DDD-Schichtrichtung (Domain hängt von niemandem; Application nicht von Infrastructure/Client) — Baseline leer.
- Namespace-Schichtsegment = Ordner-Schicht (inkl. „Layer-Omission"-Erkennung, mit diskriminierenden Theory-Fällen).
- Max. 1000 Zeilen/Datei — Baseline leer.
- Keine privaten verschachtelten Typen — Baseline leer.
- Kein stummer `catch` — Baseline mit 22 inventarisierten Altlasten (`EngineeringRulesTests.cs:146-177`).
- Kein `GetAwaiter().GetResult()` im Produktcode — Baseline mit 4 Dispose-/Transport-Pfaden.

**Lücke**: Regex-basiert (z. B. erfasst der Catch-Check keine `catch`-Blöcke mit nur-Rethrow-Logik); prüft `samples/`, das Verzeichnis heißt aber `examples/` (`EngineeringRulesTests.cs:117` — Examples werden vom 1000-Zeilen-Gate faktisch nicht erfasst, allerdings `tests/` schon).

### 2.2 CalloraVoipSdk.Core.IntegrationTests (231 Dateien, ~34,8 k Zeilen)

**Zweck**: Das Schwergewicht der Suite — RFC-orientierte Protokoll- und Komponententests auf L0–L3, meist mit echten UDP-Sockets auf Loopback, per `InternalsVisibleTo` unter der Facade (`src/Core/Properties/AssemblyInfo.cs:5`). **Parallelisierung assembly-weit deaktiviert** mit ausführlicher Begründung (Timer-/ThreadPool-Kontention auf CI-Runnern, `TestParallelization.cs`).

**Harness-/Hilfsklassen** (alle vollständig gelesen):
- `CapturingSipTransportRuntime` (212 Z.): Fake-`ISipTransportRuntime`, das Requests aufzeichnet und per `ResponseFactory`/`ProvisionalResponsesFactory` Antworten (inkl. 100rel-Provisionals vor der Final-Response) einspielt; Fehlerinjektion über `ThrowOnSendMethod`/`ThrowOnSendPredicate`.
- `CapturingSipServerTransactionEngine` / `NoopSipServerTransactionEngine`: zeichnen gesendete Response-Statuscodes auf bzw. no-op.
- `AckTestSipCallSessionContext` (180 Z.): kompletter Fake-`ISipCallSessionContext` für Dialog-/ACK-Tests.
- `RawTurnUdpClient` (239 Z.): minimaler Single-Socket-TURN-Client mit stabilem 5-Tupel für Server-E2E (Allocate/Permission/ChannelBind/Data-Indication), spricht das echte Wire-Format.
- `CapturingLogger`, `CapturedSipRequest`, `NoopSipDigestAuthenticator`, `NoopDisposable`, `DelegateDisposable`.

**Thematische Gruppierung der Testdateien** (vollständige Zuordnung der Dateinamen):
- **SIP-Signaling (~55 Dateien)**: Registrierung/Expires/Unregister (`SipOutboundRegistrationTests`, `SipRegistrationExpiresTests`, `SipRegistrationPasswordOptionalTests`, `SipLineChannelUnregisterTests`, `PhoneLineUnregisterContractTests`); Digest-Auth inkl. DoS-/Replay-Härtung (`SipDigestAuthenticationTests`, `SipDigestChallengeSelectorTests`, `SipNonceCountTests`, `SipDigestAuthIntAndReasonTests`, `SipInviteAuth422ReplayTests`, `SipInviteDigestRetryDosTests`, `SipRegistrationDigestRetryTests`, `SipTransportConnectionDosTests`); Dialog/Routing (`SipDialogIdentity*`, `SipDialogRouteHeaderTests`, `SipInDialog*`, `SipUacRouteSetTests`, `SipRecordRouteResponseTests`, `SipInDialogDigestStrictRouteTests`); INVITE-Lebenszyklus/Races (`SipInviteSuccessAckTests`, `SipCancelRaceTests`, `SipCancelTransactionTests`, `SipMergedInviteTrackerTests`, `SipActiveInviteStateConsistencyTests`, `SipCallSessionAdvertisedContactRaceTests`); 100rel/PRACK (`SipInvitePrackChainTests`, `SipReliableProvisional*`, `ReliableProvisionalOptInTests`); Transfer (`SipAttendedTransferReplacesTests`, `SipReferProgressNotifyTests`); Session-Timer (`SipSessionTimer*`); Transportwahl/TLS/WS/SRV (`SipTransportSelectionTests`, `SipTlsTargetHostTests`, `SipWebSocketSubprotocolTests`, `SipSrvWeightedOrderingTests`, `SipWireStreamFramerTests`); NAT/Contact/rport (`SipRportContact*`, `SipPublicContactTests`, `SipPublicMediaAddressTests`); Wire-Robustheit + Log-Redaction (`SipWireRobustnessTests`, `SipWireTraceRedactionTests`); Sonstiges (`SipCustomHeaderTests`, `SipRemoteIdentityTests`, `SipProtocolViaParameterTests`, `SipCoreCallChannelRekeyTests`, `SipOutboundCallStartedEventTests`, `SipServerTransactionViaReflectionTests`, `TrunkInboundMatcherTests`).
- **RTP/Jitter (~20)**: `RtpSession*` (SSRC-Cap/-Kollision, Timestamp-Reserve, DTLS/STUN-Demux, Secondary-Stream), `RtpSymmetricLatchTests`, `RtpTelephoneEventCodecTests`, `RtpPooledReceiveBufferTests`, Header-Extensions (`OneByteRtpHeaderExtensionsTests`, `RtpMidHeaderExtensionTests`, `RtpRidHeaderExtensionTests`, `RtpOutboundHeaderExtensionStamperTests`), `RtpTransmissionGateTests`, `SendDrainGateTests`, `JitterBuffer*` (Overflow, RTT-Seed, Stalled-Timestamp), `MediaPacketClassifierTests`, `RtxRetransmissionTests`.
- **RTCP/QoS (~8)**: `Rtcp*` (Compound-/XR-Decode, Feedback-Codecs, Transmissionsintervall, CNAME), `QosMetricsTests`.
- **SRTP/SRTCP/Krypto (L1, ~12)**: `Srtp*`/`Srtcp*` (Kontexte, Multi-SSRC, Media-Path, Reoffer-Kontinuität, Signaling→Media-E2E), `SrtpHardeningTests`, `AesCmCipherTests`, `SlidingReplayWindowTests`.
- **DTLS (~5)**: `DtlsSrtpHandshakeTests`, `DtlsMediaPathE2eTests`, `DtlsSignalingToMediaE2eTests`, `DtlsCertificateFromX509Tests`, `ServerFingerprintValidationTests` (fail-closed-Garantien explizit getestet).
- **SDP (~10)**: Codec-Präferenz, BUNDLE/mid/extmap, msid, Origin-Version, SDES-/DTLS-Profil-Antworten, Simulcast-Wire, Parse-Failure-Logging.
- **ICE/STUN (~25)**: Check-List, Scheduler, Consent-Freshness (RFC 7675), Nomination, Role-Conflict/Tie-Breaker, Restart-Detektor, Inbound-Handler, `Stun*` (Codec-Wire-Bounds, Shared-Socket, Server-Host-E2E, Auth-Posture).
- **TURN (~22)**: Client (Allocator, Permission/Channel-Refresh-Loops, Even-Port, Don't-Fragment, Indication-Channel, Send-Path) und **eigener TURN-Server** (E2E, Quota, TCP/TLS-Control, Stream-Framer-Bounds), `CompositeRelayKeepAliveTests`.
- **BUNDLE-Media-Engine (~26)**: `Bundled*` — Media-Session/Factory/Builder, DTLS-Keying, ICE-Control(+Relay), Inbound-/Outbound-Pipeline, RTCP-Reporter, RTP-Demultiplexer, Track-Router, Video-Simulcast, Stats, Keep-Alive, Relay-Transitionen.
- **Congestion Control (~16)**: `TransportCc*` (Arrival-Recorder, Delay-Trend, Loss-Estimator, Feedback-Builder/-Correlator/-Interpreter/-Sender, Send-History, E2E-Wiring), `CongestionBitrateControllerTests`, `TransportWideCcTests`.
- **Video (~21)**: Packetisation/Depacketiser, Reorder-Buffer, RTX (Send/Receive, SDES-Varianten), Keyframe-/Loss-Feedback (PLI/FIR), SDP-/extmap-Negotiation, ICE-Attachment, Media-Stream-E2E.
- **WebRTC-Peer (Kern, ~15)**: `WebRtcPeerConnectionTests`, `WebRtcPeerLoopbackTests`, `WebRtcPeerToPeerTests` (zwei Peers, DTLS-SRTP + ICE-Consent + Audio/Video beidseitig), Srflx-/Relay-Gathering, Opus-/Audio-/Video-Negotiation, Simulcast-Offer, Session-Factory.
- **Call-/Client-Orchestrierung (~12)**: `CallIceAgentTests`, `CallDisposeHangupFaultTests`, `CallMediaOrchestratorVideoWiringTests`, `CallMediaTapContractTests`, `CallObservabilitySnapshotTests`, `PhoneLineHangupObservationTests`, `VoipClientModuleRegistrationSafetyTests`, `BridgeAudioTranscodingTests`, `DefaultVideoConvenienceTests`, `PublicVideoTapContractTests`, `VideoDevicePortContractTests`, `OpusCodecTests`/`OpusDeviceCodecTests`, `AdvertisedMediaAddressResolverTests`.

**Auffällige Lücken**: kein Fuzzing im engeren Sinn (nur handkuratierte Malformed-Inputs); die Suite ist trotz „IntegrationTests"-Namen überwiegend komponentennah — echte L4-Zwei-Instanzen-E2E über `VoipClient` fehlt hier (nur im schmalen InteropTests-Projekt).

### 2.3 CalloraVoipSdk.Client.Tests (21 Dateien, ~2.100 Zeilen)

**Zweck**: Facade-/DI-/Options-Ebene (L4-Oberfläche ohne Netz). Gruppen:
- **VoipClient-Facade**: `VoipClientDisposeTests`, `IVoipClientMockabilityTests` (Reflection-Gate: jede Manager-Property auf `IVoipClient` muss ein Interface sein), `ModuleRegistryTests` (Register/Get/TryGet, DI-Autoattach), `TurnServerHostTests`, `DefaultVideoConvenienceFacadeTests`.
- **Options/Mapping**: `VoipOptionsValidatorTests`, `VoipOptionsMappingTests`, `VoipOptionsMappingCompletenessTests` — Letzterer ist ein Reflection-**Drift-Guard**: Neue `VoipOptions`-Felder ohne Mapping in `VoipConfiguration` schlagen automatisch fehl (Schutz vor „silent default").
- **WebRTC-Facade (4.6-Preview)**: `WebRtcClientTests`, `WebRtcDependencyInjectionTests` (`AddCalloraVoip(...).AddWebRtc(...)`), `WebRtcFacadeCorrectnessTests`, `WebRtcModuleRegistryTests`, `WebRtcSignalingTests`, `WebRtcTrickleTests`/`WebRtcTrickleSignalingTests`, `WebRtcSimulcastConfigTests`, `WebRtcStatsTests`, `WebRtcRecordingTests`, `PeerConnectionManagerTests`, `MediaTapSetTests`, `RemoteTrackSetTests`.

**Lücke**: Die klassischen Call-Control-Convenience-APIs (`ConnectAsync`, `DialAndWaitUntilConnectedAsync`, `OnIncomingCall`) werden hier kaum direkt getestet — sie hängen an Core.IntegrationTests bzw. am einzigen Interop-Test.

### 2.4 CalloraVoipSdk.Audio.Tests (4 Testdateien — sehr schmal)

Gezielte Evidenz-Tests für Audits: `PcmGainTests` (In-Place-Gain äquivalent zum allozierenden Legacy-Oracle, HARD-F1), `BoundedPlaybackBufferTests` (Drop-Oldest-Bounded-Buffer, HARD-F4), `G722CodecCachingTests` (Codec-Caching verhaltensidentisch + Allokationsmessung), `SmokeTests`. **Lücke**: keine Tests für Device-Hot-Switch, Mute/Volume-Runtime-Control, Formatwechsel oder die Windows-/Linux-Gerätepfade selbst (plattformbedingt schwer, aber ungetestete öffentlich beworbene Features).

### 2.5 CalloraVoipSdk.InteropHarness (Bibliothek, kein Testprojekt)

Wiederverwendbares öffentliches Harness (per `InternalsVisibleTo` in Core eingetragen, dokumentiert als F001). Klassen (alle vollständig gelesen):
- **Media**: `RtpMediaLoopback` — L2-Fixture: zwei echte `RtpCallMediaSession` über UDP-Loopback, Matrix-fähig (PCMU/Opus × Plain/SRTP-SDES, `LoopbackConfig`), Port-Kollisions-Retry, `RoundTripAsync`, `RunAndCollectQualityAsync`.
- **Metrics**: `ResourceSampler` (Managed-Heap, PrivateMemory, WorkingSet, Threads, Handles, FDs/Sockets via `/proc/self/fd` mit Sentinel −1 außerhalb Linux, inkl. Selbst-FD-Korrektur), `ResourceSample`, `TrendAssertions` (Median-Sockel-Vergleich + **OLS-Steigungs-Regression** `NoUpwardSlope`), `ResourcePlateauAssertions` (absolute Plateau-Toleranz für Zähler-Ressourcen), `TrendResult`, `PlateauResult`, `MediaQualitySnapshot`.
- **Signaling**: `SipRegisterLoopHarness` (L3: echter `SipLineChannel`-Refresh-Loop gegen `RecordingRegistrationService`-Fake-Registrar mit kurzem Expires), `RegisterCycle`, `NoopCallSignaling`/`NoopSdpNegotiatorStub`.
- **Audit/Diagnostics**: `SoakArtifactSink` (env-gated über `SOAK_ARTIFACT_DIR`; JSON-Messreihe + `summary.md`-Zeile je Lauf, Commit-SHA aus `GITHUB_SHA`), `SoakRunReport`, `Finding`/`AuditFindingFormatter` (Markdown-Zeilen fürs Register), `SoakFailure`/`SoakFailureReport` (strukturierte Fehlschläge statt nackter Zähler).

### 2.6 CalloraVoipSdk.SoakTests

**Zweck**: Leak-, Drift- und Lifecycle-Soaks + Unit-Tests des Harness selbst (das Harness ist selbst getestet — gut). Parallelisierung deaktiviert (prozessweite Messungen). `SoakProfile` liefert `Short` (PR-CI) und `Long` (nightly, Env-übersteuerbar: `SOAK_ITERATIONS`, `SOAK_WAVES`, `SOAK_PARALLELISM`, `SOAK_DURATION_SECONDS`).
- **Soak-Szenarien**: `RtpMediaLeakSoakTests` (serielle Loopback-Zyklen; Warm-up unbemessen, dann OLS-Slope auf Managed/Private-Memory + absolute Plateaus für Threads/Sockets/FDs — Socket-Toleranz 0), `ConcurrentLoopbackSoakTests` (parallele Wellen, strukturierte `SoakFailure`-Diagnose), `MediaQualityDriftSoakTests` (volle Matrix Codec×Security; Overflow==0; Jitter-Drift; F002-Repro geskippt; F004-Wächter), `SipLineChannelRefreshLifecycleSoakTests` (Kadenz, monotone CSeq, stabile Call-ID + Ressourcenreihe; XML-Doc mit expliziter „Scope-Ehrlichkeit").
- **Harness-Unit-Tests**: `Metrics/*` (TrendAssertions, NoUpwardSlope, Plateau, Sampler), `Audit/*` (Formatter, ArtifactSink), `Diagnostics/*`, `Media/*` (Loopback-Round-Trip/Parallel-Start/Quality-Collection), `Signaling/SipRegisterLoopHarnessTests`.
- Artefakte werden **vor** den Assertions geschrieben, damit auch Fehlläufe ihre Messreihe hinterlassen.

### 2.7 CalloraVoipSdk.InteropTests (4 Dateien — minimal)

**Zweck**: L4-Interop gegen echten Fremd-Stack. `AsteriskContainer` (Testcontainers, `andrius/asterisk:22`, minimale PJSIP-Konfiguration mit Digest-Auth-Endpoint 6001, Wait auf „Asterisk Ready."), `DockerRequiredFactAttribute` (Ping-Probe, Skip statt Fail ohne Docker-Daemon), `AsteriskContainerSmokeTests` und **genau ein** echter Interop-Test: `AsteriskRegisterInteropTests` — SDK-REGISTER-Flow (401-Challenge → 200 OK) über die Container-Bridge-IP. **Lücke**: kein Call-/Media-/DTMF-/Transfer-Interop; nur Registrierung. Die Roadmap (README:481) räumt das ein.

## 3. Performance-Projekte & Baselines (vollständig gelesen)

- **CalloraVoipSdk.Core.Performance**: handgerollter Microbenchmark-Runner (kein BenchmarkDotNet) für drei CORE-010-Hotpaths: `srtp.protect_unprotect.roundtrip`, `sip.stream_framer.frame_parse`, `rtp.packet_codec.decode`. Methodik: 2 Warm-up-Runden, 6 Iterationen, GC-Zwangs-Collect vor jeder Iteration, Messung `Stopwatch` (ns/op) + `GC.GetAllocatedBytesForCurrentThread` (B/op). CLI: `--write-baseline <path>`, `--gate <path>`, `--max-regression-percent` (Default **15 %**). Gate vergleicht je Case Zeit UND Allokation gegen die Baseline; fehlende Cases und ungültige Baselines schlagen fehl (Exit-Code 1).
- **Baseline**: `perf/baselines/core-performance-baseline.json` — erfasst 2026-04-14 auf .NET 8.0.26; Zeitwerte sind auffällig runde Zahlen (52.000/12.000/340 ns), die Allokationswerte exakt — vermutlich manuell geglättete Zeit-Deckel.
- **CalloraVoipSdk.Conferencing.Performance**: gleicher Runner-Aufbau, misst `Pcm16ConferenceMixer.TryMixForTarget` für n=2/4/8 Teilnehmer. **Defekt/verwaist**: referenziert `src/Modules/Conferencing/CalloraVoipSdk.Conferencing.csproj`, das **im Repo nicht existiert** (`perf/CalloraVoipSdk.Conferencing.Performance/CalloraVoipSdk.Conferencing.Performance.csproj:5`), und ist nicht in der Solution — baut nicht. Zudem inkonsistente CLI-Flagge (`--gate-baseline` statt `--gate`). Keine Baseline vorhanden.
- **CalloraVoipSdk.Media.Performance**: reines Skelett (`Program.cs:7` druckt nur „skeleton"), aber in der Solution.
- **Regressionserkennung in der Praxis**: Der Gate-Mechanismus ist sauber gebaut, wird aber **von keinem CI-Workflow aufgerufen** (kein `perf`-Bezug in `.github/workflows/`) — Perf-Regressionen werden derzeit nicht automatisch erkannt.

## 4. Beispiele (vollständig gelesen)

| Sample | Zeigt | Qualität |
|---|---|---|
| **BasicCalling** (318 Z.) | Interaktives Softphone: Registrierung, Dial/Accept/Reject/Hangup, Default-Audio | Hoch: Passwort-Maskierung, Audio-Attach bewusst off-signaling-thread (`Task.Run`), sauberes Cleanup, `-v`-Logging |
| **Dialer** (137 Z.) | Sequenzieller Kampagnen-Dialer mit Ergebnisübersicht und Exit-Codes | Solide, args- oder interaktiv |
| **Transfer** (184 Z.) | Blind- (REFER) und Attended-Transfer mit Rückfrage-Anruf | Solide |
| **CustomAudio** (203 Z.) | Media-Tap ohne Hardware: Frame-Statistik + selbst erzeugter 440-Hz-PCMU-Ton (eigene µ-law-Kodierung) | Hoch; demonstriert korrekt „nie auf dem Medienpfad blockieren" (Interlocked/Volatile) |
| **Switchboard** (419 Z.) | Mehrere gleichzeitige Calls; Verbinden via Attended-Transfer **oder** MediaConnector-Bridge; Audio-Fokus | Das anspruchsvollste Sample; Thread-Kommentare (Signaling-Thread), Bridge-Ressourcen-Teardown bei Call-Ende |
| **VideoCalling** (224 Z.) | Öffentliche Video-API transport-only: `IVideoReceiver`/`IVideoSender`, `RecommendedBitrateChanged` → `StubVideoEncoder` | Didaktisch stark; markiert explizit, dass die App den Codec mitbringt |
| **WebRtcPeer** (92 Z.) | Zwei WebRTC-Peers über In-Memory-Signalling, `ConnectAsync`, `TrackReceived`, `IMediaTap` | Kompakt, gut kommentiert |
| **WebRtcRecording** (89 Z.) | L3-Media-Tap als Mini-Recording-Modul | Kompakt |
| **WebRtcDependencyInjection** (35 Z.) | `AddCalloraVoip(...).AddWebRtc(...)`, Auflösung `IWebRtcClient` aus DI | Minimal, zweckerfüllend |
| **WebRtcVideoCall.Web** (Program.cs 92 Z. + app.js 67 Z. + index.html) | ASP.NET-WebSocket-Signalling-Relay + **natives Browser-WebRTC** (getUserMedia/RTCPeerConnection); Glare-Vermeidung per Lock dokumentiert | Ehrlich dokumentiert: **der SDK-Peer ist hier NICHT im Medienpfad** — Browser-Interop ist ein offener Meilenstein |

Alle Samples multi-targeten net8/9/10, sind in der Solution und referenzieren das SDK per `ProjectReference`. `examples/commercial/README.md` dokumentiert bewusst nicht committete Paid-Module-Samples. **Kleinere Lücke**: Die Tabelle in `examples/README.md:6-12` listet nur die 5 SIP-Samples; VideoCalling und die vier WebRtc-Samples fehlen dort (im Root-README aber genannt).

## 5. Build / CI / Packaging / Doku

**Toolchain**: `global.json` pinnt SDK **10.0.100** (`rollForward: latestFeature`); alle Bibliotheken/Tests/Beispiele targeten `net8.0;net9.0;net10.0` (Ausnahme: ArchitectureTests nur net10.0). `Directory.Solution.props` aktiviert Static-Graph-Restore. `src/Directory.Build.props`: Version-Fallback **4.6.0-preview.1** (Release-Version kommt per `/p:Version` aus dem Tag), SourceLink-artige Einstellungen (`EmbedUntrackedSources`, `snupkg`, `ContinuousIntegrationBuild`), Analyzer-Level `8.0-recommended` mit umfangreicher `NoWarn`-Liste (u. a. CA5350/CA5351 — bewusst, da SIP-Digest MD5/SHA1 braucht), README+LICENSE ins Paket.

**Workflows**:
- **ci.yml**: Matrix ubuntu+windows; .NET 8/9/10; Build mit `CodeAnalysisTreatWarningsAsErrors=true`; dann **explizit zuerst die Architektur-Gates**; dann Solution-Tests mit Filter `FullyQualifiedName!~CalloraVoipSdk.Core.Tests & Category!=SoakLong & Category!=Interop` inkl. Coverage (Cobertura-Artefakt, kein Schwellwert-Gate). Separater `interop`-Job (ubuntu) führt die Docker/Asterisk-Tests mit `-f net10.0` aus.
- **soak.yml**: nightly (03:00 UTC) + `workflow_dispatch` mit Inputs `iterations`/`duration_seconds`; Inputs bewusst nur über `env:` (Injection-Hygiene dokumentiert); führt `Category=SoakLong` auf net10.0 aus; `SOAK_ARTIFACT_DIR` aktiviert den Artifact-Sink; Artefakte werden `if: always()` hochgeladen.
- **packages.yml**: Tag `v*` oder Dispatch mit Versions-Input; Release-Gate `dotnet build` + `dotnet test` (ohne Filter!); packt 6 Pakete (Core, CalloraVoipSdk, Client, Audio.Abstractions, Audio.Windows, Audio.Linux) und pusht nach nuget.org (`NUGET_API_KEY`, `--skip-duplicate`).
- **docs.yml**: PR-/main-Gate für den DocFX-Build (Site nur als Artefakt, 7 Tage).
- **release-docs.yml**: bei Push auf main; extrahiert Version aus Tag oder `VersionPrefix`, injiziert sie per `jq` in `docfx.json`, deployt nach GitHub Pages (root + versioniertes Unterverzeichnis, `.nojekyll`).

**DocFX**: lokales Tool `docfx 2.78.5` (`.config/dotnet-tools.json`); Metadata nur aus **Core + Client** (net8.0/Release); `filterConfig.yml` blendet `[Obsolete]`, `Core.Infrastructure.*` und `Core.Application.Ports.*` aus der API-Doku aus — konsistent zur „Public API boundary" im README. Portal-Inhalte unter `docs/portal/` (Guides, Concepts, Interop, Commercial).

**Lizenz**: Apache-2.0 (Copyright Bechstein.Digital Ecommerce UG). `THIRD-PARTY-NOTICES.md` ist vorbildlich: Tabelle mit Version/Lizenz/Copyright (BouncyCastle, Concentus, DnsClient.NET, NAudio, PortAudioSharp2, Microsoft.Extensions.*), explizit „kein Copyleft", Test-only-Pakete ausgenommen.

## 6. Qualitätsbefunde

**Stärken**
1. Außergewöhnliche Test-Engineering-Kultur: L0–L4-Modell, lebendes Audit-Register mit adversarialer Verifikation und Datei:Zeile-Evidenz, „Scope-Ehrlichkeit" in Soak-Docs, Wächter-Tests für dokumentierte Schichtgrenzen (F004), Skip-Tests, die nach einem Fix automatisch zu Fix-Verifikationen werden (F002).
2. Architektur-Gates mit „Baseline darf nur schrumpfen"-Mechanik (`SourceScan.AssertMatchesBaseline`) — verhindert sowohl neue Verstöße als auch veraltete Baselines; als eigener CI-Schritt vor den Tests.
3. Soak-Methodik auf hohem Niveau: Warm-up-Verwerfung, OLS-Steigung statt Start/Ende-Vergleich, absolute Plateaus für Zähler-Ressourcen (Socket-Toleranz 0), strukturierte Fehlschläge, env-gated Artefakt-Sink vor den Assertions.
4. Drift-Guards per Reflection (`VoipOptionsMappingCompletenessTests`, `IVoipClientMockabilityTests`) gegen schleichende API-/Mapping-Erosion.
5. Beispiele sind ehrlich (Transport-only-Video, SDK nicht im Browser-Medienpfad) und lehren die kritischen Verhaltensregeln (nicht auf Medienpfad/Signaling-Thread blockieren).

**Lücken & Risiken**
1. **Perf-Gate nicht in CI verdrahtet**: Kein Workflow ruft `--gate` auf; die Baseline (`perf/baselines/core-performance-baseline.json`) ist maschinen-/runtime-gebunden (.NET 8.0.26, 2026-04-14) und veraltet gegenüber der net10-Toolchain. Regressionserkennung existiert nur auf dem Papier.
2. **Conferencing.Performance ist kaputt/verwaist**: `ProjectReference` auf nicht existentes `src/Modules/Conferencing/…` (`CalloraVoipSdk.Conferencing.Performance.csproj:5`), nicht in der Solution, keine Baseline. `Media.Performance` ist ein leeres Skelett (`Program.cs:7`), steht aber in der Solution.
3. **Release-Gate läuft ungefiltert**: `packages.yml:50` (`dotnet test CalloraVoipSdk.sln` ohne `Category!=SoakLong`) führt beim Release die Long-Soaks mit Default 500 Iterationen × alle TFMs aus — langsam und flaky-anfällig; inkonsistent zur CI-Filterung.
4. **Interop-Abdeckung minimal**: nur REGISTER gegen Asterisk (`tests/CalloraVoipSdk.InteropTests/Registration/AsteriskRegisterInteropTests.cs`); kein Call-/Media-/DTMF-/Transfer-/TLS-Interop. Ohne Docker im Runner skippen die Tests still (grün) — der `interop`-Job kann „bestehen", ohne etwas zu prüfen.
5. **Stale-Referenzen**: CI-Filter schließt das nicht existente `CalloraVoipSdk.Core.Tests` aus (`ci.yml:44`); `InternalsVisibleTo` für nicht existente Assemblies `CalloraVoipSdk.Tests`, `Core.Tests`, `Conferencing.Tests` (`src/Core/Properties/AssemblyInfo.cs:3-6`) — Letzteres weitet die Internals-Sichtbarkeit unnötig auf kapern-bare Assembly-Namen aus (unsigniert).
6. **Audio-Schicht dünn getestet**: 4 Testdateien für zwei Plattform-Backends; beworbene Runtime-Features (Hot-Switch, Mute, Volume, Formatwechsel) ohne Testabdeckung; CI testet keine echte Audio-Hardware (erwartbar, aber nirgends kompensiert, z. B. durch Headless-Contract-Tests gegen `Audio.Headless`).
7. **Coverage ohne Gate**: Cobertura wird nur als Artefakt hochgeladen; der README-Badge ist statisch; kein Mindest-Coverage-Check.
8. **Bekannte offene Produktdefekte**: F002 (QoS-Metrik `PacketsUnrecoverableLoss` auf Loopback falsch, `RtpCallMediaSession.cs:540-558` laut Register) offen; F003 (fehlende Zeit-Abstraktion) begrenzt die Aussagekraft der Signaling-Soaks strukturell.
9. **Kleinigkeiten**: `EngineeringRulesTests.cs:117` scannt `samples/` statt `examples/`; `examples/README.md`-Tabelle unvollständig (Video-/WebRtc-Samples fehlen); ArchitectureTests laufen in `ci.yml` doppelt (eigener Schritt + ungefilterter Solution-Lauf); WebRTC-Facade lt. README/CHANGELOG „not yet browser-validated" — das Web-Sample umgeht den SDK-Peer bewusst.

**Gesamteinschätzung**: Eine überdurchschnittlich reife, selbstkritische Test- und CI-Landschaft mit klarem Ebenenmodell und starker Regressions-Hygiene; die Hauptschwächen liegen in der Ausführung der Randbereiche — nicht verdrahtetes Perf-Gate, verwaiste Perf-Projekte, minimale echte Interop-Tiefe, ungefiltertes Release-Gate und dünne Audio-Abdeckung.
