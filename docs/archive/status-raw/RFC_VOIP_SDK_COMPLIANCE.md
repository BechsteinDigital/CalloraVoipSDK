# VoIP SDK – Vollständige RFC-Compliance-Referenz

Stand: 2026-04-09 (aktualisiert) | Codebase-Analyse: 230+ Quelldateien, 36 Test-Suites, C#/.NET 8.0

Dieses Dokument ist die verbindliche RFC-Referenz für die Weiterentwicklung des VoIP SDKs.
Es umfasst RFC 3261 (SIP Core) mit Kapitelstatus, alle relevanten Update-RFCs zu 3261,
sowie alle Must-Have-RFCs für SIP, SDP, RTP, SRTP, ICE, STUN, TURN, NAT, Codecs und Security.

---

## Compliance-Ziel

- RFC 3261 Core vollständig und belastbar implementiert.
- Alle RFC-3261-Kapitel, die durch spätere RFCs geändert/erweitert wurden, sind nach dem neuesten Stand zu bewerten.
- Relevante Update-RFCs sind eingearbeitet.
- Bewusst gewählte Erweiterungen sind als „Erledigt" oder „Extern" dokumentiert.
- Nicht umgesetzte Themen sind als „Offen" oder „Out-of-Scope" markiert.
- Kapitel gelten nur dann als `Erledigt`, wenn Core + relevante Updates nachweisbar abgedeckt sind.

---

## Legende

| Status         | Bedeutung                                                                |
|----------------|--------------------------------------------------------------------------|
| Erledigt       | Vollständig und belastbar umgesetzt, getestet                            |
| Teilweise      | Basis vorhanden, Vollabdeckung oder Corner-Cases fehlen                  |
| Extern         | Via SIPSorcery 10.0.3 delegiert (DTLS/Codecs)                           |
| Offen          | Nicht oder kaum begonnen                                                 |
| Out-of-Scope   | Bewusst nicht umgesetzt (z.B. Proxy, S/MIME, WebRTC-SM)                  |
| N/A            | Dokumentations-/Referenzabschnitt, kein Implementierungsgegenstand       |

---

## Architektur-Übersicht (Kurzfassung)

```
Domain Layer:        Call.cs, PhoneLine.cs, CallState.cs, LineState.cs
Application Layer:   CallManager.cs, PhoneLineManager.cs, MediaManager.cs
Infrastructure:
  ├── Sip/Wire/      SipWireProtocol.cs, SipWireStreamFramer.cs, SipHeaderNames.cs
  ├── Sip/Tx/        SipClientTransactionExecutor.cs, SipServerTransactionEngine.cs
  ├── Sip/Transport/ SipTransportRuntime.cs, SipWebSocketConnection.cs, TlsConfiguration.cs
  ├── Sip/Signaling/ SipCallSession.cs, SipDialogManager.cs, SipDialogPath.cs
  │   ├── Auth/      SipDigestAuthentication.cs
  │   ├── Identity/  SipAssertedIdentityHeader.cs, ISipIdentityTrustPolicy.cs
  │   ├── Reliability/ SipReliableProvisionalManager.cs
  │   ├── Sessions/  SipSessionTimerManager.cs, SipSessionTimerPolicy.cs
  │   └── Subscriptions/ SipSubscriptionLifecycleManager.cs
  ├── Sip/Routing/   SipDnsRouteResolver.cs
  ├── Sdp/
  │   ├── Models/    SdpSessionDescription.cs, SdpMediaDescription.cs, SdpCodecDefinition.cs, …
  │   ├── Parsing/   SdpSessionParser.cs, SdpSessionSerializer.cs (IPv4+IPv6)
  │   └── OfferAnswer/ SdpOfferAnswerNegotiator.cs, SdpOfferAnswerResult.cs
  ├── Rtp/
  │   ├── Packets/   RtpPacket.cs, RtpExtension.cs
  │   ├── Wire/      RtpPacketCodec.cs (RFC 3550 §5.1, Extensions, Padding)
  │   ├── Session/   RtpSession.cs, RtpSequenceValidator.cs (RFC 3550 §A.1), RtpSessionOptions.cs
  │   └── Profile/   RtpAvpProfile.cs (RFC 3551, PT 0–127, Clock-Rates)
  ├── Srtp/
  │   ├── Crypto/    SrtpCryptoSuite.cs, SrtpKeyMaterial.cs, SrtpKeyDerivation.cs (RFC 3711 §4.3)
  │   └── Context/   SrtpContext.cs (AES-CM, HMAC-SHA1, Replay-Fenster), ISrtpContext.cs
  ├── Stun/          StunClient.cs, StunServer.cs, StunMessageCodec.cs (36 Dateien)
  └── Audio/         PlatformAudioDeviceFactory.cs, G711Codec.cs (Windows/Linux)
Extern (SIPSorcery): DTLS/DTLS-SRTP, Video-Codecs
```

---

## Teil 1: RFC 3261 – SIP Core Kapitelstatus

Quelle: https://datatracker.ietf.org/doc/html/rfc3261

**Hinweis zu Updates:** Mehrere RFC-3261-Kapitel wurden durch spätere RFCs geändert oder erweitert.
Diese sind in der Spalte „RFC-Updates" kenntlich gemacht. Die Kapitel gelten erst dann als vollständig erledigt,
wenn auch die jeweiligen Update-RFCs berücksichtigt sind.

| Abschnitt | Titel | Erledigt | Stand | RFC-Updates | Hinweis |
|---|---|---|---|---|---|
| 1 | Introduction | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 2 | Overview of SIP Functionality | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 3 | Terminology | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 4 | Overview of Operation | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 5 | Structure of the Protocol | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 6 | Definitions | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 7 | SIP Messages | Ja | Erledigt | RFC 5118 (WS-Encoding-Fälle) | Konsistent abgeschlossen: 7.1–7.5 umgesetzt und per Compliance-Tests abgesichert. |
| 7.1 | Requests | Ja | Erledigt | – | Request-Line-Regeln aus RFC 3261 §7.1 umgesetzt (single SP, SIP/2.0, Method-Token, Request-URI-Validierung), inkl. Wire-Compliance-Tests. `SipWireProtocol.cs` |
| 7.2 | Responses | Ja | Erledigt | – | Status-Line-Regeln umgesetzt. `SipWireProtocol.cs` |
| 7.3 | Header Fields | Ja | Erledigt | RFC 8217 (name-addr-Pflicht), RFC 7230 (Obs-Fold deprecated) | Header-Folding, Duplicate-Row, Compact-Form. RFC 8217 name-addr teilweise durchgesetzt. `SipHeaderRowRules.cs`, `SipHeaderValueStorage.cs` |
| 7.3.1 | Header Field Format | Ja | Erledigt | RFC 7230 | Header-Format inkl. LWS, Folded-Lines, Non-Combine-Ausnahmen für Auth-Header. |
| 7.3.2 | Header Field Classification | Ja | Erledigt | – | Request-/Response-only Header beim Parsing korrekt ignoriert. |
| 7.3.3 | Compact Form | Ja | Erledigt | – | Long/Compact Formen normalisiert inkl. `e`/Content-Encoding. |
| 7.4 | Bodies | Ja | Erledigt | – | Body-Verarbeitung gehärtet: typed body, leere Body-Sonderfälle, Serialisierungsvalidierung. |
| 7.4.1 | Message Body Type | Ja | Erledigt | – | Nicht-leerer Body erfordert Content-Type. |
| 7.4.2 | Message Body Length | Ja | Erledigt | – | Byte-genaue Content-Length-Verarbeitung inkl. CL=0, kein Transfer-Encoding: chunked. |
| 7.5 | Framing SIP Messages | Ja | Erledigt | RFC 7118 (WS-Framing), RFC 5626 (Keepalive/CRLF-Ping) | Stream-Framing: führende CRLF ignoriert, strict Content-Length-Framing; CRLF-Pong (RFC 5626 §4.4.1) implementiert. `SipWireStreamFramer.cs`, `SipStreamConnection.cs` |
| 8 | General User Agent Behavior | Ja | Erledigt | – | UAC §8.1, UAS §8.2, §8.3 vollständig implementiert. §8.2.7 N/A (stateful UA). |
| 8.1 | UAC Behavior | Ja | Erledigt | – | UAC §8.1 abgeschlossen: Request-Generierung, Routing/Sende-Fallback, Response-Policy inkl. Redirect/4xx/Transportfehler. `SipCallSessionTransactionService.cs` |
| 8.1.1 | Generating the Request | Ja | Erledigt | RFC 5626 (Outbound: Ob-Route), RFC 5627 (GRUU in Contact) | §8.1.1-Headerregeln umgesetzt; preloaded Route-Set (strict/loose). `SipCallSessionHeaderService.cs` |
| 8.1.1.1 | Request-URI | Ja | Erledigt | – | Validierung SIP/SIPS, strict/loose Routing. |
| 8.1.1.2 | To | Ja | Erledigt | – | Out-of-dialog Requests ohne To-Tag. |
| 8.1.1.3 | From | Ja | Erledigt | RFC 4474/8224 (Identity/PASSporT) | UAC erzeugt From mit lokalem Tag. `SipProtocol.cs` |
| 8.1.1.4 | Call-ID | Ja | Erledigt | – | Stabiler Call-ID über Retry-Pfade (Auth/423). |
| 8.1.1.5 | CSeq | Ja | Erledigt | – | CSeq-Methodenabgleich und Sequenznummer-Validierung. |
| 8.1.1.6 | Max-Forwards | Ja | Erledigt | – | UAC setzt Max-Forwards=70. |
| 8.1.1.7 | Via | Ja | Erledigt | RFC 3581 (rport-Parameter) | Via-Branch mit RFC 3261 Magic-Cookie; UAC setzt `;rport`; UAS reflektiert `rport=<port>` und `received=<ip>` in Responses. `SipProtocol.ReflectViaRport` — belegt durch `SipViaReflectionAndEscalationTests` (received bei abweichender Quelle, rport-Füllung, unverändert bei Treffer, idempotent bei doppelter Anwendung), `SipCallSignalingService`, `SipCallSessionHeaderService` |
| 8.1.1.8 | Contact | Ja | Erledigt | RFC 5627 (GRUU), RFC 5626 (Outbound/reg-id) | INVITE trägt genau einen Contact; SIPS-Contact-Erzwingung. `SipCallSessionHeaderService.cs` |
| 8.1.1.9 | Supported and Require | Ja | Erledigt | – | Require/Proxy-Require; 420-Retry mit token-basiertem Filtering. `SipRequireOptionPolicy.cs` |
| 8.1.1.10 | Additional Message Components | Ja | Erledigt | RFC 4028 (Session-Timer), RFC 3323 (Privacy) | UAC erzeugt methodenspezifische Komponenten. |
| 8.1.2 | Sending the Request | Ja | Erledigt | RFC 3263 (DNS-Lookup), RFC 5626 (Flow-Token) | UAC sendet über RFC 3263-Kandidatenlisten, Failover, Redirect-Zielverfolgung. `SipDnsRouteResolver.cs` — Kandidatenbildung belegt durch `SipDnsRouteResolverRfc3263Tests` |
| 8.1.3 | Processing Responses | Ja | Erledigt | – | §8.1.3.1–8.1.3.5 implementiert. `SipClientTransactionExecutor.cs` |
| 8.1.3.1 | Transaction Layer Errors | Ja | Erledigt | – | 408/503-Synthese bei Timeout/Transportversagen. |
| 8.1.3.2 | Unrecognized Responses | Ja | Erledigt | – | Normalisierung auf Klassen-x00 / 183. |
| 8.1.3.3 | Vias | Ja | Erledigt | – | Responses mit >1 Via verworfen. |
| 8.1.3.4 | Processing 3xx Responses | Ja | Erledigt | – | Contact-Ziele aus 3xx, Duplikat-Schutz, rekursiver Retry. |
| 8.1.3.5 | Processing 4xx Responses | Ja | Erledigt | – | 401/407/413/415/416/420 mit Retry-Pfaden. `SipCallSessionTransactionService.cs` |
| 8.2 | UAS Behavior | Nein | Teilweise | – | Kernverhalten abgesichert; §8.2.2.1/§8.2.6.2 implementiert und per Compliance-Tests abgedeckt. |
| 8.2.1 | Method Inspection | Ja | Erledigt | – | Unbekannte Methoden → 501 mit Allow-Header. `SipCallSignalingService.cs`; `SipUasSection8ComplianceTests.cs` |
| 8.2.2 | Header Inspection | Ja | Erledigt | RFC 8217 (name-addr) | Pflichtheader (Via/From/To/Call-ID/CSeq) geprüft → 400; Ingress-Vollabdeckung. |
| 8.2.2.1 | To and Request-URI | Ja | Erledigt | – | 416 bei unsupported URI Scheme; 404 bei unbekanntem User via `ISipUasUserIdentityPolicy` (ADR-001). Default: AcceptAll (backwards-compatible). `SipCallSignalingService.cs`. Belegt durch `ServedUserSipIdentityPolicyTests` (gedientes AoR unabhaengig von der Schreibweise, nicht gedienter User abgelehnt, leere Menge verweigert statt still alles zu blockieren). |
| 8.2.2.2 | Merged Requests | Ja | Erledigt | – | Merged INVITE mit gleicher Call-ID/From-tag/CSeq aber unterschiedlichem Via-branch → 482. `SipMergedInviteTracker.cs` |
| 8.2.2.3 | Require | Ja | Erledigt | – | Unsupported Require-tags → 420 mit Unsupported-Header. `SipRequireOptionPolicy.cs` |
| 8.2.3 | Content Processing | Ja | Erledigt | – | Content-Type ≠ application/sdp → 415 mit Accept; Content-Encoding ≠ identity → 415 mit Accept-Encoding. `SipContentPolicy.cs` |
| 8.2.4 | Applying Extensions | Ja | Erledigt | – | Via Require-Header-Prüfung (§8.2.2.3) abgedeckt. |
| 8.2.5 | Processing the Request | Ja | Erledigt | – | Methoden-Dispatch vollständig; INVITE/OPTIONS/dialog-scoped/unknown korrekt behandelt. |
| 8.2.6 | Generating the Response | Ja | Erledigt | – | Pflicht-Header, To-Tag-Regeln, Record-Route-Kopie implementiert. |
| 8.2.6.1 | Sending a Provisional Response | Ja | Erledigt | RFC 3262 (PRACK für zuverlässige Provisionals) | 100 Trying sofort bei INVITE-Eingang; RFC 3262 (100rel) via `SipReliableProvisionalManager.cs`. |
| 8.2.6.2 | Headers and Tags | Ja | Erledigt | – | To-Tag in allen non-100-Responses; kein To-Tag in 100; Record-Route verbatim aus Request kopiert. `SipIngressResponseHeaders.Create` |
| 8.2.7 | Stateless UAS Behavior | – | N/A | – | SDK ist ein stateful UAS (Server-Transaction-Layer). §8.2.7 gilt ausschließlich für stateless UAS. Retransmit-Absorption (äquivalenter Schutz) durch `SipServerTransactionEngine` per §17.2.1 abgedeckt. `SipUasSection8ComplianceTests.cs` |
| 8.3 | Redirect Servers | Ja | Erledigt | – | `ISipCallSession.RedirectAsync` sendet 3xx (300–399) mit Contact-URIs; Record-Route wird nicht weitergeleitet; To-Tag gesetzt; Dialog → Terminated. Unterstützte Codes: 300/301/302/305/380. ADR-002. `SipUasRedirectComplianceTests.cs` |
| 9 | Canceling a Request | Nein | **Erledigt** | – | UAC + UAS CANCEL vollständig. ADR-005. `SipSection9CancelComplianceTests.cs` |
| 9.1 | Client Behavior | Nein | **Erledigt** | – | CANCEL Via-Branch ≠ INVITE-Branch (`NewBranch()`); CSeq matcht INVITE; Request-URI matcht INVITE. Gate vor `SendInviteTransactionAsync` freigegeben → `HangupAsync` kann CANCEL ohne Deadlock senden. ADR-005. |
| 9.2 | Server Behavior | Nein | **Erledigt** | – | CANCEL→487 mit Reason-Header-Weiterleitung. `SipCallSessionInboundService.HandleInboundRequestAsync` (CANCEL-Zweig mit Transaktionsabgleich). Belegt durch `SipCancelTransactionMatchTests`. |
| 10 | Registrations | Nein | **Erledigt** | RFC 5626 (Outbound/reg-id: **Erledigt**), RFC 5627 (GRUU: Offen), RFC 3327 (path), RFC 3608 (Service-Route) | Vollständige UAC-Seite implementiert. `SipSection10RegisterComplianceTests.cs` |
| 10.1 | Overview | Nein | **Erledigt** | – | REGISTER/Unregister/Fetch-Bindings-Lifecycle via `ISipRegistrationService`. |
| 10.2 | Constructing the REGISTER Request | Nein | **Erledigt** | RFC 5626 §4 (+sip.instance, reg-id), RFC 3327 (Supported: path) | Via/From/To/Call-ID/CSeq/Contact/Expires/Max-Forwards/User-Agent/Supported korrekt; `+sip.instance` + `reg-id=1` wenn `InstanceId` gesetzt; `Supported: outbound` automatisch hinzugefügt wenn `InstanceId` gesetzt. Belegt durch `SipOutboundRegistrationTests` (Contact trägt ob/instance/reg-id aus `SipRegistrationRequest.InstanceId`; ob hängt hinter einem bestehenden transport-Parameter; ohne Instanz-Id keine outbound-Parameter). Geprüft ist der Contact-Aufbau aus der Property, nicht der vollständige REGISTER-Versand. |
| 10.2.1 | Adding Bindings | Nein | **Erledigt** | – | `RegisterAsync`; Expires-Header + Contact-`expires`-Parameter (§10.2.1.1). |
| 10.2.1.1 | Setting the Expiration Interval | Nein | **Erledigt** | – | Contact: `<uri>;expires=N` gesetzt; Min-Expires-Retry bei 423. EffectiveExpires aus 200 OK extrahiert (Expires-Header oder Contact-Param). |
| 10.2.1.2 | Preferences among Contact Addresses | Nein | Teilweise | RFC 3840/3841 (UA-Capabilities/Caller-Prefs) | `q`-Parameter (Contact-Preferenz) nicht implementiert; kein Mehrfach-Contact. |
| 10.2.2 | Removing Bindings | Nein | **Erledigt** | – | `UnregisterAsync` (per-binding `Expires: 0`); `UnregisterAllAsync` (`Contact: *; Expires: 0`). |
| 10.2.3 | Fetching Bindings | Nein | **Erledigt** | – | `FetchBindingsAsync` sendet REGISTER ohne Contact; Bindungen aus 200-OK-Contact in `RegisteredBindings` geparst. |
| 10.2.4 | Refreshing Bindings | Nein | **Erledigt** | – | Call-ID-Persistenz via `ExistingCallId`/`StartCSeq`; `SipLineChannel` bewahrt Call-ID + CSeq über Refresh-Zyklen. `NextCSeq` im Result. |
| 10.2.5 | Setting the Internal Clock | Nein | Teilweise | – | Date-Header aus 200 OK nicht ausgewertet (kein RTP-Zeitstempel-Sync erforderlich auf UAC-Seite). |
| 10.2.6 | Discovering a Registrar | Nein | **Erledigt** | RFC 3263 (DNS-basierte Registrar-Ermittlung) | DNS-Routing via `SipDnsRouteResolver.cs`; Route-Candidates; SIPS-Scheme-Erkennung. Belegt durch `SipDnsRouteResolverRfc3263Tests`. |
| 10.2.7 | Transmitting a Request | Nein | **Erledigt** | – | `SipClientTransactionExecutor`; Timeout-Handling; Retry für mehrere Route-Kandidaten. |
| 10.2.8 | Error Responses | Nein | **Erledigt** | – | 401/407 Digest-Retry; 423 Min-Expires; 3xx Redirect; 413/415/416 Fallback; Telemetrie-Events. |
| 10.3 | Processing REGISTER Requests | Nein | N/A | – | UAS/Registrar-Seite: SDK ist ein User Agent, kein Registrar — Out-of-Scope. |
| 11 | Querying for Capabilities | Ja | **Erledigt** | RFC 3840 (Capabilities in Contact) | `HandleOptionsAsync` in `SipCallSignalingService.cs`; 200 OK mit Allow/Supported/User-Agent-Headern; UAC sendet OPTIONS für Keepalive. |
| 11.1 | Construction of OPTIONS Request | Ja | **Erledigt** | – | OPTIONS-Request korrekt konstruiert; Max-Forwards, Via, From, To, Call-ID, CSeq. |
| 11.2 | Processing of OPTIONS Request | Ja | **Erledigt** | – | Inbound OPTIONS → 200 OK mit Allow/Supported; kein Dialog erforderlich. |
| 12 | Dialogs | Nein | **Erledigt** | RFC 6141 (re-INVITE/Target-Refresh), RFC 5057 (Multiple Dialog Usages) | Dialog-Basis vollständig via `SipDialogManager.cs`, `SipDialogPath.cs`; Target-Refresh getestet; Forking-Early-Randfälle nicht produktiv relevant. |
| 12.1 | Creation of a Dialog | Nein | **Erledigt** | – | Inbound + Outbound INVITE-Dialog-Erzeugung; Tags, Route-Set, Remote-Target in `SipDialogManager.ApplyInviteResponse`. |
| 12.1.1 | UAS behavior | Nein | **Erledigt** | – | Inbound INVITE: localTag, remoteTag, Remote-Target via Contact. |
| 12.1.2 | UAC Behavior | Nein | **Erledigt** | – | Outbound INVITE: frühe Dialoge (1xx), bestätigter Dialog (2xx). |
| 12.2 | Requests within a Dialog | Nein | **Erledigt** | RFC 6141 (re-INVITE-Semantik neu geregelt) | re-INVITE, BYE, INFO, DTMF alle in-dialog korrekt geroutet; CSeq-Validierung. |
| 12.2.1 | UAC Behavior | Nein | **Erledigt** | – | `SipCallSession.HoldAsync/UnholdAsync/HangupAsync`; in-dialog Request-URI = RemoteTargetUri. |
| 12.2.1.1 | Generating the Request | Nein | **Erledigt** | – | Via/From/To/Call-ID/CSeq/Route-Set korrekt befüllt. |
| 12.2.1.2 | Processing the Responses | Nein | **Erledigt** | RFC 6141 | Target-Refresh: re-INVITE 200 OK mit neuem Contact aktualisiert RemoteTargetUri → BYE geht an neue URI. Belegt durch `SipByeAndTargetRefreshComplianceTests` (Contact-Wechsel bewegt das Remote-Target; fehlgeschlagene Antwort und Nicht-Refresh-Methode bewegen es nicht). |
| 12.2.2 | UAS Behavior | Nein | **Erledigt** | – | In-dialog INVITE (re-INVITE) auto-beantwortet; CSeq-Monotonie-Check; Glare → 491. |
| 12.3 | Termination of a Dialog | Nein | **Erledigt** | – | BYE terminiert Dialog; CANCEL abbricht INVITE-Transaktion; `SipCallSession.TransitionTo(Terminated)`. |
| 13 | Initiating a Session | Nein | **Erledigt** | RFC 3262 (PRACK), RFC 6026 (2xx-Handling), RFC 6141 (re-INVITE) | INVITE-Grundfluss vollständig. `SipSection13SessionEstablishmentComplianceTests.cs` |
| 13.1 | Overview | Nein | **Erledigt** | – | – |
| 13.2 | UAC Processing | Nein | **Erledigt** | RFC 3262, RFC 6026 | – |
| 13.2.1 | Creating the Initial INVITE | Nein | **Erledigt** | RFC 3264 (SDP Offer/Answer), RFC 4028 (Session-Timer) | Pflicht-Header (Via/From/To/CSeq/Contact/Content-Type), SDP, Session-Timer. |
| 13.2.2 | Processing INVITE Responses | Nein | **Erledigt** | RFC 6026 | – |
| 13.2.2.1 | 1xx Responses | Nein | **Erledigt** | RFC 3262 (Reliable Provisionals / PRACK) | 100 Trying → kein StateChanged; 180 Ringing → State=Ringing; RFC 3262 PRACK via `SipReliableProvisionalManager.cs`. |
| 13.2.2.2 | 3xx Responses | Nein | **Erledigt** | – | Contact-Extraktion, Duplikatsuppression, 3xx ohne Contact → SipFinalResponseException. |
| 13.2.2.3 | 4xx, 5xx and 6xx Responses | Nein | **Erledigt** | – | ACK gesendet für 486/603/500; SipFinalResponseException propagiert. |
| 13.2.2.4 | 2xx Responses | Nein | **Erledigt** | RFC 6026 (UAC muss ACK für jede 2xx senden, auch für Forking) | ACK branch ≠ INVITE branch; Forking-2xx: ACK+BYE für nicht-gewählte Forks. `SipForkedInviteUacComplianceTests.cs`. |
| 13.3 | UAS Processing | Nein | **Erledigt** | RFC 3262, RFC 6026 | – |
| 13.3.1 | Processing of the INVITE | Nein | **Erledigt** | – | – |
| 13.3.1.1 | Progress | Nein | **Erledigt** | RFC 3262 (100rel-Provisional-Retransmission) | 100 Trying sofort; RFC 3262-Retransmission via `SipReliableProvisionalManager.cs`. |
| 13.3.1.2 | The INVITE is Redirected | Nein | **Erledigt** | – | `ISipCallSession.RedirectAsync` → 3xx. |
| 13.3.1.3 | The INVITE is Rejected | Nein | **Erledigt** | – | 486 Busy Here via `HangupAsync` während Ringing. |
| 13.3.1.4 | The INVITE is Accepted | Nein | **Erledigt** | RFC 6026 (UAS muss 2xx retransmit bis ACK) | 200 OK mit Contact-Header; 2xx-Retransmit via `SipServerTransactionEngine.ArmInviteSuccessRetransmit`. |
| 14 | Modifying an Existing Session | Nein | **Erledigt** | RFC 6141 (re-INVITE vollständig neu spezifiziert), RFC 3311 (UPDATE-Methode) | RFC 6141 re-INVITE vollständig; UPDATE implementiert; hold/unhold/Target-Refresh getestet. `SipSection14And15ComplianceTests.cs` |
| 14.1 | UAC Behavior | Nein | **Erledigt** | RFC 6141 | `HoldAsync/UnholdAsync`: re-INVITE gesendet, 200 OK auto-ACK, Target-Refresh angewandt. |
| 14.2 | UAS Behavior | Nein | **Erledigt** | RFC 6141 | Inbound re-INVITE auto-beantwortet mit 200 OK; hold (a=sendonly/inactive) → OnHold; unhold → Established; RemoteHoldChanged-Event. `SipCallSessionInboundService.cs` |
| 15 | Terminating a Session | Nein | **Erledigt** | – | BYE + CANCEL vollständig; alle normativen Pfade abgesichert. `SipSection14And15ComplianceTests.cs`, `SipSection9CancelComplianceTests.cs` |
| 15.1 | Terminating a Session with a BYE Request | Nein | **Erledigt** | – | Outbound BYE (`HangupAsync`); inbound BYE auto-beantwortet mit 200 OK → Terminated. |
| 15.1.1 | UAC Behavior | Nein | **Erledigt** | – | `HangupAsync` sendet BYE; bei pending re-INVITE → CANCEL statt BYE (ADR-005). |
| 15.1.2 | UAS Behavior | Nein | **Erledigt** | – | Inbound BYE → 200 OK → Terminated; auch aus OnHold-Zustand. Belegt durch `SipByeAndTargetRefreshComplianceTests` (200 OK + Terminated, aus Established und aus OnHold). |
| 16 | Proxy Behavior | Nein | Out-of-Scope | RFC 5393 (Loop-Detection), RFC 7339 (Overload Control) | SDK fokussiert auf User Agent, nicht Proxy. |
| 16.1–16.12.1.3 | (alle Proxy-Abschnitte) | Nein | Out-of-Scope | – | Out-of-Scope für initiale Lieferung. |
| 17 | Transactions | Nein | **Erledigt** | RFC 4320 (Non-INVITE-Timer-Änderungen), RFC 6026 (2xx/INVITE-Transaction) | Client- und Server-Transaction vollständig. `SipClientTransactionComplianceTests.cs`, `SipInviteServerTransactionComplianceTests.cs` |
| 17.1 | Client Transaction | Nein | **Erledigt** | RFC 4320, RFC 6026 | `SipClientTransactionExecutor.cs` |
| 17.1.1 | INVITE Client Transaction | Nein | **Erledigt** | RFC 6026 | Timer A/B/D; INVITE stoppt Retransmit nach 1xx; Auto-ACK für 3xx-6xx. |
| 17.1.1.1 | Overview of INVITE Transaction | Nein | **Erledigt** | – | Vollständig implementiert. |
| 17.1.1.2 | Formal Description | Nein | **Erledigt** | RFC 6026 | State machine: Calling → Proceeding → Completed/Terminated. |
| 17.1.1.3 | Construction of the ACK Request | Nein | **Erledigt** | RFC 6026 | Auto-ACK mit gleichem Via-Branch für 3xx-6xx; TU sendet ACK mit neuem Branch für 2xx. |
| 17.1.2 | Non-INVITE Client Transaction | Nein | **Erledigt** | RFC 4320 (Timer E/F, T2-Anpassung) | Timer E/F/K; T1→T2-Doubling; RFC 4320 Timer-Schedule vollständig. |
| 17.1.2.1 | Overview of the non-INVITE Transaction | Nein | **Erledigt** | RFC 4320 | Vollständig implementiert. |
| 17.1.2.2 | Formal Description | Nein | **Erledigt** | RFC 4320 | Trying → Proceeding (T2-Interval) → Completed (Timer K) → Terminated. |
| 17.1.3 | Matching Responses to Client Transactions | Nein | **Erledigt** | – | Call-ID/CSeq/Via-Branch; Multi-Via-Discard (§8.1.3.3); Response-Normalisierung (§8.1.3.2). |
| 17.1.4 | Handling Transport Errors | Nein | **Erledigt** | – | Send-Fehler propagieren als Exception; Transaction terminiert sofort. |
| 17.2 | Server Transaction | Nein | **Erledigt** | RFC 4320 (NIST-Timer-Änderungen) | `SipServerTransactionEngine.cs`, `SipServerTransactionState.cs` |
| 17.2.1 | INVITE Server Transaction | Nein | **Erledigt** | RFC 6026, RFC 3262 | Timer G/H (Failure-Retransmit), Timer L (Success-Retransmit), Timer I (ACK-Cleanup); ACK-Matching mit anderem Branch. |
| 17.2.2 | Non-INVITE Server Transaction | Nein | **Erledigt** | RFC 4320 (NIST: Timer J für TCP/TLS auf 0) | Response-Snapshot gespeichert; Timer J=32s (UDP), 0 (TCP/TLS). |
| 17.2.3 | Matching Requests to Server Transactions | Nein | **Erledigt** | – | `SipServerTransactionKey.cs`; RFC 3261 Branch+SentBy; Legacy-RFC2543-Fallback. |
| 17.2.4 | Handling Transport Errors | Nein | **Erledigt** | – | Transport-Fehler bei Retransmit terminiert Transaction; `RegisterTransportErrorHandler`-Callback für TU-Benachrichtigung. |
| 18 | Transport | Nein | Teilweise | RFC 7118 (WebSocket), RFC 5626 (Outbound), RFC 4168 (SCTP) | UDP/TCP/TLS/WS/WSS implementiert. §18.1.1/§18.1.2/§18.2.1/§18.2.2/§18.3/§18.4 erledigt. RFC 5626 reg-id+outbound erledigt; flow-token (Server-seitig) out-of-scope. |
| 18.1 | Clients | Nein | **Erledigt** | – | §18.1.1: UDP→TCP-Eskalation >1300 Bytes. §18.1.2: UAC sendet `;rport`, Responses kommen per Transaction-Matching (branch) zurück. |
| 18.1.1 | Sending Requests | Nein | **Erledigt** | RFC 3263 | Transport-Selektion via DNS-Kandidaten; Nachrichten >1300 Bytes werden von UDP auf TCP eskaliert, Via-Token wird aktualisiert (`EscalateViaTransportToTcp` — belegt durch `SipViaReflectionAndEscalationTests`). |
| 18.1.2 | Receiving Responses | Nein | **Erledigt** | RFC 3581 (rport-Symmetric-Response) | UAC setzt `;rport` in Via; Responses treffen per UDP-Socket bzw. gleicher TCP-Verbindung ein; Transaction-Matching via branch. |
| 18.2 | Servers | Nein | **Erledigt** | – | §18.2.1: `received=` immer hinzugefügt wenn Quell-IP ≠ sent-by; §18.2.2: Routing per Via-Parametern. |
| 18.2.1 | Receiving Requests | Nein | **Erledigt** | RFC 3581, RFC 5626 | `SipProtocol.ReflectViaParameters`: `received=<ip>` wenn Quell-IP ≠ sent-by (RFC 3261 §18.2.1 MUST); `rport=<port>` wenn bare `;rport` vorhanden (RFC 3581 §4). CRLF-Pong (RFC 5626 §4.4.1) gesendet. |
| 18.2.2 | Sending Responses | Nein | **Erledigt** | RFC 3581 | `SipProtocol.ResolveUdpResponseDestination`: Routing nach `received`/`rport`/sent-by aus Via. `SipServerTransactionEngine.SendResponseAsync` löst Ziel-Endpoint per RFC §18.2.2-Algorithmus. |
| 18.3 | Framing | Nein | **Erledigt** | RFC 7118, RFC 5626 | `SipWireStreamFramer.cs`; CRLF-Pong in `SipStreamConnection.cs`; WS-Framing via `SipWebSocketConnection.cs`. Content-Length-basiertes Stream-Framing für TCP/TLS. |
| 18.4 | Error Handling | Nein | **Erledigt** | – | Stale-Stream-Verbindung bei Send-Fehler entfernt; einmaliger Retry mit neuer Verbindung (`SendPayloadAsync` in `SipTransportRuntime.cs`). |
| 19 | Common Message Components | Nein | **Erledigt** | RFC 8217, RFC 4475 (Torture Tests) | §19.1–§19.3 vollständig. `SipProtocol.cs` |
| 19.1 | SIP and SIPS Uniform Resource Indicators | Nein | **Erledigt** | RFC 5630 (SIPS-URI-Klarstellungen) | §19.1.1–§19.1.6 erledigt. |
| 19.1.1 | SIP and SIPS URI Components | Nein | **Erledigt** | – | `SipUriProtocol.TryParseSipUri`: user, host, port, scheme; URI-Parameter (transport, lr, maddr) — belegt durch `SipUriComparisonTests` (die zehn Beispielpaare aus RFC 3261 §19.1.4, deren Vergleich genau diese Parameter liest) und `SipUriEncodingTests`. Transport-Ableitung siehe §19.1.5. |
| 19.1.2 | Character Escaping Requirements | Nein | **Erledigt** | – | `SipUriProtocol.SipUriEncodeUser` / `SipUriDecodeUser`: RFC-konforme Percent-Encoding/Decoding für User-Info-Teil. Belegt durch `SipUriEncodingTests` (§25.1-Zeichenklassen, UTF-8-Oktett-Escaping, Round-Trip). |
| 19.1.3 | Example SIP and SIPS URIs | - | N/A | – | Beispielabschnitt. |
| 19.1.4 | URI Comparison | Nein | **Erledigt** | – | `SipUriProtocol.SipUriEqual`, angewandt von `ServedUserSipIdentityPolicy` (§8.2.2.1). Schema/Host case-insensitiv, User case-sensitiv inkl. Unreserved-Escape-Normalisierung; Port und `transport` werden **wie angegeben** verglichen (weggelassen ≠ explizit Default); `user`/`ttl`/`method`/`maddr` einseitig ⇒ ungleich, alle übrigen Parameter einseitig ⇒ ignoriert; URI-Header vollständig. Belegt durch `SipUriComparisonTests` gegen die zehn Beispielpaare aus §19.1.4. **Korrektur 2026-08-18:** die vorige Fassung war als erledigt geführt, verletzte aber fünf dieser zehn Beispiele (Port- und transport-Defaults wurden aufgelöst, unbekannte Parameter zählten einseitig, Escapes wurden nicht normalisiert) und hatte keinen Aufrufer und keinen Test. |
| 19.1.5 | Forming Requests from a URI | Nein | **Erledigt** | – | `TryParseSipUri` + DNS-Auflösung via `SipDnsRouteResolver` — belegt durch `SipDnsRouteResolverRfc3263Tests` (NAPTR-Order entscheidet, Service-Feld bestimmt den Transport, SIPS+D2T→TLS); Transport-Parameter aus URI ausgewertet. |
| 19.1.6 | Relating SIP URIs and tel URLs | Nein | **Erledigt** | RFC 3966 (The tel URI) | `SipUriProtocol.TryTelUriToSipUri`: `tel:+1-800-…` → `sip:+1800…@domain;user=phone`; visuelle Trennzeichen normalisiert; phone-context-Parameter entfernt. Belegt durch `SipUriEncodingTests` (RFC-3966-visual-separator, phone-context, Negativfälle). |
| 19.2 | Option Tags | Nein | **Erledigt** | – | `SipRequireOptionPolicy` — belegt durch `SipViaReflectionAndEscalationTests` (unterstützte Tags akzeptiert, unbekannte einzeln und dedupliziert im Unsupported-Header, fehlender Header kein Verstoß): Require-Validierung mit Supported={100rel, timer, replaces}; 420+Unsupported bei unbekannten Tags; Supported-Header gesetzt. |
| 19.3 | Tags | Nein | **Erledigt** | – | `SipProtocol.NewTag()` → GUID-basiert (global unique, cryptographically random per RFC §19.3); From-Tag (UAC) / To-Tag (UAS) korrekt gesetzt; Dialog-Matching über From/To-Tags. |
| 20 | Header Fields | Nein | Teilweise | RFC 8217 (name-addr/addr-spec für viele Header) | Viele Header implementiert; RFC 8217 name-addr-Pflicht teilweise durchgesetzt. |
| 20.1 | Accept | Nein | **Erledigt** | – | `Accept: application/sdp` in UAC INVITE-Requests. `SipCallSessionHeaderService.CreateDialogRequestHeaders`. `SipSection20HeaderComplianceTests.cs` |
| 20.2 | Accept-Encoding | Nein | Teilweise | – | Offen. |
| 20.3 | Accept-Language | Nein | Teilweise | – | Offen. |
| 20.4 | Alert-Info | Nein | Teilweise | RFC 7462, RFC 8597 | Offen. |
| 20.5 | Allow | Nein | Teilweise | – | Offen. |
| 20.6 | Authentication-Info | Nein | Teilweise | RFC 7615 | Offen. |
| 20.7 | Authorization | Nein | Teilweise | RFC 7235, RFC 7616 | Digest Auth MD5/SHA-256/SHA-512-256 implementiert. `SipDigestAuthentication.cs` |
| 20.8 | Call-ID | Nein | Teilweise | – | Offen. |
| 20.9 | Call-Info | Nein | Teilweise | – | Offen. |
| 20.10 | Contact | Nein | Teilweise | RFC 8217, RFC 5627 (GRUU), RFC 5626 | Contact generiert; GRUU fehlt. |
| 20.11 | Content-Disposition | Nein | **Erledigt** | – | `Content-Disposition: session` bei allen SDP-Requests/-Responses (UAC INVITE, UAS INVITE-Responses). `SipCallSessionHeaderService`. `SipSection20HeaderComplianceTests.cs` |
| 20.12 | Content-Encoding | Nein | Teilweise | – | Offen. |
| 20.13 | Content-Language | Nein | Teilweise | – | Offen. |
| 20.14 | Content-Length | Nein | Teilweise | – | Byte-genaue CL-Verarbeitung implementiert. |
| 20.15 | Content-Type | Nein | Teilweise | – | Offen. |
| 20.16 | CSeq | Nein | Teilweise | – | CSeq-Methodenabgleich validiert. |
| 20.17 | Date | Nein | **Erledigt** | – | `Date: <RFC 1123>` in allen UAS-Responses (SHOULD per RFC §20.17). `SipCallSessionHeaderService.CreateResponseHeadersFromRequest`, `SipIngressResponseHeaders.Create`. `SipSection20HeaderComplianceTests.cs` |
| 20.18 | Error-Info | Nein | Teilweise | – | Offen. |
| 20.19 | Expires | Nein | Teilweise | – | Offen. |
| 20.20 | From | Nein | Teilweise | RFC 8217, RFC 4474/8224 | From mit Tag generiert. |
| 20.21 | In-Reply-To | Nein | Teilweise | – | Offen. |
| 20.22 | Max-Forwards | Nein | Teilweise | – | Max-Forwards=70 gesetzt. |
| 20.23 | Min-Expires | Nein | Teilweise | – | Offen. |
| 20.24 | MIME-Version | Nein | Teilweise | – | Offen. |
| 20.25 | Organization | Nein | Teilweise | – | Offen. |
| 20.26 | Priority | Nein | Teilweise | RFC 6878, RFC 4412 | Offen. |
| 20.27 | Proxy-Authenticate | Nein | Teilweise | RFC 7235, RFC 7616 | Digest Auth Challenge-Handling via `SipCallSessionTransactionService.cs` |
| 20.28 | Proxy-Authorization | Nein | Teilweise | RFC 7235, RFC 7616 | 407-Retry implementiert. |
| 20.29 | Proxy-Require | Nein | Teilweise | – | Offen. |
| 20.30 | Record-Route | Nein | Teilweise | RFC 8217, RFC 5630 | Route-Set-Handling via `SipDialogPath.cs` |
| 20.31 | Reply-To | Nein | Teilweise | RFC 8217 | Offen. |
| 20.32 | Require | Nein | Teilweise | – | Offen. |
| 20.33 | Retry-After | Nein | Teilweise | – | UAC: Retry-After aus 503/486/600-INVITE-Responses wird geparst und geloggt. REGISTER: Retry-After in Exception-Nachricht inkludiert. Vollständige Exponierung über `ICall` als öffentliche API fehlt noch. `SipCallSessionTransactionService`, `SipRegistrationService`. `SipSection20HeaderComplianceTests.cs` |
| 20.34 | Route | Nein | Teilweise | RFC 8217, RFC 5626 | In-Dialog-Routing via `SipDialogPath.cs` |
| 20.35 | Server | Nein | **Erledigt** | – | `Server: <UserAgent>` in allen UAS-Responses. `SipCallSessionHeaderService.CreateResponseHeadersFromRequest`, `SipIngressResponseHeaders.Create`. `SipSection20HeaderComplianceTests.cs` |
| 20.36 | Subject | Nein | Teilweise | – | Offen. |
| 20.37 | Supported | Nein | **Erledigt** | – | `Supported: 100rel, timer, replaces` in allen UAC-Requests (INVITE, REGISTER+path) und UAS-Responses. `SipCallSessionHeaderService`, `SipRegistrationService`, `SipCallSignalingService`. `SipSection20HeaderComplianceTests.cs` |
| 20.38 | Timestamp | Nein | Teilweise | – | Offen. |
| 20.39 | To | Nein | Teilweise | RFC 8217 | To mit/ohne Tag korrekt gesetzt. |
| 20.40 | Unsupported | Nein | Teilweise | – | Offen. |
| 20.41 | User-Agent | Nein | Teilweise | – | Offen. |
| 20.42 | Via | Nein | Teilweise | RFC 3581 (rport), RFC 5626 (alias) | Via-Branch mit Magic-Cookie, rport erkannt. |
| 20.43 | Warning | Nein | Teilweise | – | Offen. |
| 20.44 | WWW-Authenticate | Nein | Teilweise | RFC 7235, RFC 7616 | 401-Challenge via `SipDigestAuthentication.cs` |
| 21 | Response Codes | Nein | Teilweise | – | Alle RFC-3261-Codes in `NormalizeUacResponseStatusCode` bekannt. Vollständiges Verhalten für: 100, 180, 200, 3xx, 401, 407, 413, 415, 416, 420, 481, 482, 486, 487, 491. Spezifisch: 488 → eigener `SipDialogTerminationReason` (SDP-Fehler). 513 korrekt normalisiert. Restliche Codes: generischer Fehler. |
| 21.1–21.6.4 | (alle Response-Code-Abschnitte) | Nein | Teilweise | (siehe oben) | Vollständig: 100/180/200/3xx/401/407/413/415/416/420/481/482/486/487/491. Spezifisch: 488 SDP-Rejection-Reason, 503/486/600 Retry-After, 513 normalisiert. Nur erkannt (generischer Fehler): 402/405/406/410/414/421/480/483–485/488/493/502/505/604/606. |
| 22 | Usage of HTTP Authentication | Nein | **Erledigt** | RFC 7235, RFC 7616, RFC 8760 | Digest Auth vollständig: MD5/MD5-sess, SHA-256/SHA-256-sess, SHA-512-256/SHA-512-256-sess, qop=auth, opaque, stale-retry, Algorithm-Präferenz (RFC 7616 §4). `SipDigestAuthentication.cs`, `SipDigestChallengeSelector.cs`. `SipDigestAuthenticationTests.cs`, `SipDigestChallengeSelectorTests.cs` |
| 22.1 | Framework | Nein | **Erledigt** | RFC 7235, RFC 7616 | WWW-Authenticate / Proxy-Authenticate Parsing; Authorization / Proxy-Authorization Generation; stale-nonce-Retry; 401/407-Retry-Flows implementiert. |
| 22.2 | User-to-User Authentication | Nein | **Erledigt** | RFC 7616 | 401-Challenge → Digest-Retry mit Authorization-Header korrekt. |
| 22.3 | Proxy-to-User Authentication | Nein | **Erledigt** | RFC 7616 | 407-Challenge → Digest-Retry mit Proxy-Authorization-Header korrekt. |
| 22.4 | The Digest Authentication Scheme | Nein | **Erledigt** | RFC 7616 (SHA-256), RFC 8760 | HA1/HA1-sess/HA2/response korrekt; alle Algorithmen implementiert; RFC 7616 §4 Algorithm-Präferenz (stärkster bei mehreren WWW-Authenticate-Headern); qop=auth-int und username*/userhash bewusst Out-of-Scope. |
| 23 | S/MIME | Nein | Out-of-Scope | RFC 5751 | Nicht implementiert, bewusst ausgelassen. |
| 23.1–23.4.3 | (alle S/MIME-Abschnitte) | Nein | Out-of-Scope | – | Out-of-Scope. |
| 24 | Examples | - | N/A | – | Dokumentations-/Referenzabschnitt. |
| 25 | Augmented BNF for the SIP Protocol | Nein | Teilweise | RFC 5234 (ABNF) | ABNF-Syntax wird größtenteils geparst; keine formale Vollabdeckung. |
| 25.1 | Basic Rules | Nein | **Erledigt** (bare LF) | RFC 5234 | RFC 3261 §7.5 MUST: Parser akzeptiert bare LF als Zeilenende. `SipWireProtocol.TrySplit` normalisiert `\r\n`→`\n` und `\r`→`\n` vor dem Parsen; `IndexOfHeaderTerminator` erkennt LFLF als Fallback-Terminator. Tests in `SipWireProtocolHardeningTests.cs`. |
| 26 | Security Considerations | Nein | Teilweise | RFC 8224, RFC 3323, RFC 3325 | TLS, Digest Auth, SIPS, Loop-Detection, Max-Forwards, Privacy implementiert; S/MIME und STIR out-of-scope. |
| 26.1.1 | Registration Hijacking | Nein | **Erledigt** | – | REGISTER mit Digest Auth (MD5/SHA-256/SHA-512-256) geschützt; TLS schützt Credentials in Transit. |
| 26.1.2 | Impersonating a Server | Nein | **Erledigt** | – | TLS mit Zertifikatvalidierung (`AcceptUntrustedCertificates = false` by default); SIPS-URI erzwingt TLS via `SipTransportRuntimeUtilities.TryInferTransportFromUri` — belegt durch `SipTransportInferenceTests` (sips: ergibt TLS auch gegen ein widersprechendes transport=tcp/udp; ws/wss werden trotz gemeinsamem Präfix unterschieden). |
| 26.1.3 | Tampering with Message Bodies | Nein | Teilweise | – | TLS-Transportschutz vorhanden; S/MIME = Out-of-scope. |
| 26.1.4 | Tearing Down Sessions | Nein | **Erledigt** | – | Dialog-Matching (Call-ID + From-tag + To-tag) in `SipDialogManager`; BYE/re-INVITE nur im etablierten Dialog erlaubt. |
| 26.1.5 | Denial of Service and Amplification | Nein | **Erledigt** | – | Max-Forwards-Validierung (483 Too Many Hops); Loop-Detection via branch-Prefix; **max. Nachrichtengröße 65 536 Bytes** in `SipWireProtocol` (DoS-Guard). Tests in `SipSection26SecurityTests.cs`. |
| 26.2.1 | Transport and Network Layer Security | Nein | **Erledigt** | RFC 5246, RFC 8446 | TLS 1.2/1.3 via `TlsConfiguration.cs`; SRTP-Policy enum (Disabled/Optional/Required); UDP/TCP/TLS/WS/WSS alle unterstützt. |
| 26.2.2 | SIPS URI Scheme | Nein | **Erledigt** | – | `SipProtocol.IsSipsUri()`; `SipTransportRuntimeUtilities.TryInferTransportFromUri` erzwingt TLS für `sips:` (belegt durch `SipTransportInferenceTests`); DNS NAPTR SIPS+D2T/SIPS+D2W in `SipDnsRouteResolver` — belegt durch `SipDnsRouteResolverRfc3263Tests` (SIPS+D2T ergibt TLS auf 5061; ein reines TLS-Angebot wird für eine Klartext-Anfrage nicht verwendet). |
| 26.2.3 | HTTP Authentication | Nein | **Erledigt** | RFC 7616 | Vollständige Digest-Implementierung: MD5, MD5-SESS, SHA-256, SHA-256-SESS, SHA-512-256, SHA-512-256-SESS, qop=auth. Algorithmus-Präferenz per RFC 7616. |
| 26.2.4 | S/MIME | Nein | Out-of-Scope | – | Out-of-Scope (siehe §23). |
| 26.3–26.3.2.4 | Implementing Security Mechanisms | Nein | Teilweise | – | Registration (26.3.2.1): Digest Auth; DoS (26.3.2.4): Max-Forwards + Loop-Detection + Message-Size-Limit. Inter-domain und Peer-to-Peer via TLS. |
| 26.4.1 | HTTP Digest Limitations | Nein | **Erledigt** | RFC 7616 | SHA-256/SHA-512-256 bevorzugt; MD5 weiterhin akzeptiert. Passwort im Speicher nur während des Calls. |
| 26.4.2 | S/MIME Limitations | Nein | Out-of-Scope | – | Out-of-Scope. |
| 26.4.3 | TLS Limitations | Nein | **Erledigt** | – | TLS konfiguriert; Zertifikatvalidierung aktiv by default. |
| 26.4.4 | SIPS URIs Limitations | Nein | **Erledigt** | – | SIPS-Scheme erkannt und erzwingt TLS. |
| 26.5 | Privacy | Nein | **Erledigt** | RFC 3323, RFC 3325 | `Privacy`-Header auf ausgehenden INVITEs über `SipInviteRequest.Privacy`; `Privacy: id` anonymisiert `From` zu `Anonymous <sip:anonymous@anonymous.invalid>` (RFC 3323 §4.1); P-Preferred-Identity koexistiert mit `Privacy: id` per RFC 3323 §5.1; `DenyAllSipIdentityTrustPolicy` schützt eingehende P-Asserted-Identity. |
| 27–30 | IANA/References | - | N/A | – | Dokumentations-/Referenzabschnitt. |

---

## Teil 2: RFC-Update-Matrix für RFC 3261

| RFC | Titel | Typ | Betroffene §§ in RFC 3261 | Priorität | Status SDK |
|---|---|---|---|---|---|
| RFC 3262 | Reliability of Provisional Responses in SIP | Updates 3261 | §8.2.6.1, §13.2.2.1, §13.3.1.1, §17.2.1 | Must Have | **Erledigt** (`SipReliableProvisionalManager.cs`) |
| RFC 3265 | SIP-Specific Event Notification | Updates 3261 | §8, §10 | Must Have → RFC 6665 | **Erledigt** (obsoleted by RFC 6665 — see RFC 6665 row) |
| RFC 3581 | SIP Extension for Symmetric Response Routing (rport) | Updates 3261 | §18.2.1, §18.2.2, §20.42 | Must Have | **Erledigt** (UAC: `;rport` in Via; UAS: `rport=` + `received=` Reflexion in Responses. `SipProtocol.ReflectViaRport`) |
| RFC 4320 | Actions Addressing Issues with SIP Non-INVITE Transaction | Updates 3261 | §17.1.2, §17.2.2 | Must Have | **Erledigt** (Timer E/F initial 500ms+Doubling→T2, Timer J TCP/TLS=0) |
| RFC 4916 | Connected Identity in SIP | Updates 3261 | §12.2 | Should Have | Offen |
| RFC 5393 | Addressing an Amplification Vulnerability in SIP | Updates 3261 | §16 (Proxy) | Should Have | Out-of-Scope (kein Proxy) |
| RFC 5621 | Message Body Handling in SIP | Updates 3261 | §7.4, §8, §20 | Should Have | **Erledigt** | §5 `handling=optional`: Requests mit unbekanntem Body-Typ und `handling=optional` werden akzeptiert (MUST NOT reject). `handling=required` oder abwesend → 415. `SipContentPolicy.IsHandlingOptional()`. multipart/mixed out-of-scope. |
| RFC 5626 | Managing Client-Initiated Connections in SIP (Outbound) | Updates 3261 | §8.1.2, §10.2, §18, §20.42 | Must Have | **Erledigt** (§4.4.1 CRLF-Pong; `+sip.instance`; `reg-id=1`; `Supported: outbound` wenn InstanceId gesetzt. `SipRegistrationService.cs`) |
| RFC 5630 | Use of the SIPS URI Scheme in SIP | Updates 3261 | §8.1.1.1, §19.1, §20.30 | Should Have | **Erledigt** (`BuildContactUri` verwendet `sips:` für TLS/WSS automatisch; INVITE-Contact SIPS-erzwungen; SIPS-URI in Routing via `SipTransportRuntimeUtilities.TryInferTransportFromUri` (belegt durch `SipTransportInferenceTests`)) |
| RFC 6026 | Correct Transaction Handling for 2xx Responses to SIP INVITE | Updates 3261 | §13.2.2.4, §13.3.1.4, §17.1.1, §17.2.1 | Must Have | **Erledigt** (2xx-Retransmit bis ACK; Auto-ACK 3xx-6xx; Forking-2xx je ACK+BYE) |
| RFC 6141 | Re-INVITE and Target-Refresh Request Handling in SIP | Updates 3261 | §12.2, §13, §14 | Must Have | **Erledigt** (re-INVITE Hold/Unhold; UPDATE; 491 Request Pending; Target-Refresh via Contact-Update in `SipDialogManager`; `SipSection14And15ComplianceTests.cs`) |
| RFC 6665 | SIP-Specific Event Notification | Obsoletes 3265 | §8, §10 | Must Have | **Erledigt** (§6.1.1 inbound NOTIFY: `NotifyReceived`-Event mit `Subscription-State`-Parsing; §4.2.2 outbound NOTIFY: `SendNotifyAsync`; §4.1 out-of-dialog SUBSCRIBE: `SubscribeAsync` mit Handle, Auto-Refresh, NOTIFY-Routing. `SipSubscriptionLifecycleManager.cs` für inbound SUBSCRIBE-Lifecycle. `SipSection6665EventNotificationTests.cs`) |
| RFC 7621 | Clarification for the Use of REFER Identity Header | Updates 3261 | §8 (mid-dialog REFER) | Nice-to-Have | Teilweise |
| RFC 8217 | Clarifications for name-addr Production in SIP | Updates 3261 | §7.3, §8.1.1, §19, §20.x | Must Have | **Erledigt** (`FormatNameAddr` mit Display-Name-Escaping (Backslash-Quotes); `ExtractUriFromNameAddr`; name-addr überall korrekt für From/To/Contact/Route) |
| RFC 8591 | SIP-Based Messaging with CPIM | Updates 3261 | §8 (MESSAGE) | Nice-to-Have | Offen |

---

## Teil 3: SIP-Erweiterungen und Companion-RFCs

### 3.1 SIP-Kernerweiterungen

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3263 | Locating SIP Servers (DNS für SIP) | Must Have | **Erledigt** | NAPTR→SRV→A/AAAA, Transport-Selektion, priority/weight-Sortierung. `SipDnsRouteResolver.cs` via DnsClient 1.8.0 — belegt durch `SipDnsRouteResolverRfc3263Tests` (ganze Kette aus konservierten Antworten, inkl. Durchfallen auf SRV bzw. A) |
| RFC 3264 | An Offer/Answer Model with SDP | Must Have | **Erledigt** | Codec-Selektion, Direction, BUNDLE/MID-Carry-through, rtcp-mux, SDES-Crypto, DTLS-Profil. `SdpOfferAnswerNegotiator.cs`. Vollständige Abdeckung in Teil 4 (SDP). |
| RFC 3311 | The SIP UPDATE Method | Must Have | **Erledigt** | UPDATE gesendet (Hold/Unhold, Session-Timer-Refresh) und empfangen; im Supported-Header; 2xx-Handling und Glare-Schutz (491). `SipCallSessionTransactionService.cs` |
| RFC 3326 | The Reason Header Field for SIP | Should Have | **Erledigt** | RFC 3326 Format: protocol/value/cause/text. `SipReasonHeader.cs`; BYE/CANCEL mit Reason-Header |
| RFC 3327 | SIP Extension for Registering Non-Adjacent Contacts (Path) | Must Have | Offen | Path-Header nicht implementiert |
| RFC 3428 | SIP Extension for Instant Messaging (MESSAGE) | Should Have | Offen | MESSAGE-Methode nicht vorhanden |
| RFC 3515 | The SIP Refer Method | Should Have | **Erledigt** | REFER gesendet/empfangen; Refer-To geparst; NOTIFY-Subscription via `SipSubscriptionLifecycleManager.cs`; norefersub erkannt (`SipRequireOptionPolicy.cs`). |
| RFC 3891 | The SIP "Replaces" Header | Should Have | **Erledigt** | Call-ID/To-Tag/From-Tag-Matching. `SipReplacesHeaderValue.cs`, `SipCallSession.MatchesReplacesTarget()` |
| RFC 3892 | The SIP Referred-By Mechanism | Should Have | **Erledigt** | `Referred-By` in ausgehendem REFER (via `SendReferAsync`); in eingehendem REFER geparst und via `SipTransferRequestedEventArgs.ReferredBy` exponiert; in INVITE via `SipInviteRequest.ReferredBy` → `SipCallSessionHeaderService`; Fallback auf From-URI wenn Header fehlt. |
| RFC 3903 | SIP Extension for Event State Publication (PUBLISH) | Should Have | Offen | PUBLISH-Methode nicht vorhanden |
| RFC 4028 | Session Timers in SIP | Must Have | **Erledigt** | Session-Expires/Min-SE, Refresher-Verhandlung, UPDATE-Refresh. `SipSessionTimerManager.cs`, `SipSessionTimerPolicy.cs` |
| RFC 5589 | SIP Call Control – Transfer | Should Have | **Erledigt** | REFER gesendet/empfangen; Referred-By (RFC 3892) vollständig; Refer-To geparst; norefersub; NOTIFY-Subscription. |
| RFC 5626 | Managing Client-Initiated Connections (Outbound) | Must Have | **Erledigt** | OutboundProxy in `SipAccount.cs`; `+sip.instance` + `reg-id=1` + `Supported: outbound` in `SipRegistrationService.cs`; CRLF-Pong. flow-token (Server-seitig) out-of-scope. |
| RFC 5627 | Obtaining and Using Globally Routable User Agent URIs (GRUU) | Should Have | Offen | GRUU nicht implementiert |

### 3.2 SIP-Erweiterungen (Should/Nice-to-Have)

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3680 | SIP Event Package for Registrations (reg) | Nice-to-Have | Offen | – |
| RFC 3840 | Indicating User Agent Capabilities in SIP | Should Have | Offen | – |
| RFC 3841 | Caller Preferences for SIP | Should Have | Offen | – |
| RFC 4412 | Communications Resource Priority for SIP | Nice-to-Have | Offen | – |
| RFC 4488 | Suppression of SIP REFER Method Implicit Subscription | Should Have | **Erledigt** | `SendReferAsync(suppressSubscription: true)` → `Refer-Sub: false` + `Require: norefersub` (Tag von `SipRequireOptionPolicy` als unterstützt geführt — belegt durch `SipViaReflectionAndEscalationTests`); eingehender REFER mit `Refer-Sub: false` unterdrückt implizites NOTIFY; `norefersub` gilt als unterstützt, ein Require darauf löst also kein 420 aus. |
| RFC 4575 | SIP Event Package for Conference State | Nice-to-Have | Offen | – |
| RFC 4662 | SIP Event Notification Extension for Resource Lists | Nice-to-Have | Offen | – |
| RFC 5057 | Multiple Dialog Usages in SIP | Should Have | Teilweise | Forking teilweise, `SipDialogManagerForkingComplianceTests.cs` |
| RFC 5839 | Conditional Event Notification in SIP | Nice-to-Have | Offen | – |
| RFC 6011 | SIP User Agent Configuration | Nice-to-Have | Offen | – |
| RFC 7092 | Taxonomy of SIP Back-to-Back User Agents | Informational | Offen | Kein normativer RFC |
| RFC 7339 | Session Initiation Protocol (SIP) Overload Control | Should Have | **Erledigt** | 503 + `Retry-After` → `SipDialogTerminationReason.RetryAfterSeconds` gesetzt; Dialer kann vor dem nächsten Versuch warten. `SipCallSessionTransactionService.cs`. |
| RFC 8262 | Content-ID Header Field in SIP | Nice-to-Have | Offen | – |

### 3.3 SIP-Transport-Erweiterungen

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 7118 | The WebSocket Protocol as a Transport for SIP | Must Have | **Erledigt** | WS/WSS via `SipWebSocketConnection.cs`, HttpListener-basiert; RFC 7118-konformes Framing |
| RFC 4168 | SCTP as a Transport for SIP | Nice-to-Have | Offen | – |
| RFC 5922 | Domain Certificates in SIP | Must Have | **Erledigt** | `SipDomainCertificateValidator.cs`: dNSName- und sip:/sips:-URI-SAN-Validierung per RFC 5922 §7.1; in `TlsConfiguration.ValidatePeerCertificateSipDomain()` integriert; in `SipTransportRuntime.ValidateTlsServerCertificate()` genutzt. Tests: `SipDomainCertificateValidatorTests.cs`. Hinweis: TargetHost-Hostname-Übergabe beim TLS-Verbindungsaufbau nutzt aktuell IP-Adresse; für vollständige SNI-Compliance ist CORE-Infrastruktur-Erweiterung nötig. |

### 3.4 SIP-Sicherheitserweiterungen

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3310 | HTTP Digest Authentication Using AKA | Nice-to-Have | Offen | AKA-Mechanismus nicht implementiert |
| RFC 3323 | A Privacy Mechanism for SIP | Must Have | **Erledigt** | `Privacy:`-Header im INVITE; `Privacy: id` → `From`-Header wird zu `Anonymous <sip:anonymous@anonymous.invalid>` anonymisiert (RFC 3323 §4.1). `SipCallSessionHeaderService.IsPrivacyIdRequested()`. |
| RFC 3325 | Private Extensions to SIP for Asserted Identity | Must Have | Teilweise | P-Asserted-Identity parsing: `SipAssertedIdentityHeader.cs`; Trust Policy: `ISipIdentityTrustPolicy.cs`, `DenyAllSipIdentityTrustPolicy.cs` |
| RFC 4474 | Enhancements for Authenticated Identity Management in SIP | Historic | Offen | Überholt durch RFC 8224; nicht neu implementieren |
| RFC 7340 | Secure Telephone Identity Problem Statement | Informational | Offen | STIR-Problemdefinition |
| RFC 8224 | Authenticated Identity Management in SIP (STIR) | Should Have | Offen | PASSporT/JWT-basierter Identity-Header nicht implementiert |
| RFC 8225 | PASSporT: Personal Assertion Token | Should Have | Offen | – |
| RFC 8226 | Secure Telephone Identity Credentials: Certificates | Should Have | Offen | – |
| RFC 8760 | The SIP Digest Access Authentication Scheme | Must Have | **Erledigt** | MD5, SHA-256, SHA-512-256 (inkl. -sess Varianten) in `SipDigestAuthentication.cs`; algorithm-Parameter negotiiert |
| RFC 7616 | HTTP Digest Access Authentication | Must Have | **Erledigt** | MD5, SHA-256, SHA-512-256, -sess Varianten, qop=auth; `SipDigestAuthentication.cs`; 401/407-Retry in `SipCallSessionTransactionService.cs` |

---

## Teil 4: SDP – Session Description Protocol

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 4566 | SDP: Session Description Protocol | Must Have | Teilweise | Parser/Serializer: o/c/m/a/v/s/t-lines, IPv4+IPv6 (IN IP4/IN IP6), CSRC, Direction. Strukturiert in Models/Parsing/OfferAnswer. `SdpSessionParser.cs`, `SdpSessionSerializer.cs` |
| RFC 8866 | SDP: Session Description Protocol (aktueller Standard 2021) | Must Have | Teilweise | Löst RFC 4566 ab; Basis (o/c/m/a/v/s/t, IPv6) vorhanden; vollständige Konformität (BUNDLE, rid, etc.) offen |
| RFC 3264 | An Offer/Answer Model with SDP | Must Have | Erledigt | Codec-Selektion, Direction, BUNDLE/MID-Carry-through, rtcp-mux, SDES-Crypto, DTLS-Profil. `SdpOfferAnswerNegotiator.cs` |
| RFC 4317 | SDP Offer/Answer Examples | Informational | N/A | Referenz-Beispiele |
| RFC 4145 | TCP-Based Media Transport in SDP | Should Have | Erledigt | `a=setup` (actpass/active/passive/holdconn) in Parser, Serializer, Offer/Answer mit Rollen-Inversion. |
| RFC 4796 | The SDP Content Attribute | Nice-to-Have | Offen | a=content nicht implementiert |
| RFC 5888 | The SDP Grouping Framework | Must Have | Erledigt | `a=group:BUNDLE`, `a=mid` parsing, Serialization und Offer/Answer carry-through. |
| RFC 5761 | Multiplexing RTP Data and Control Packets (RTCP-MUX) | Must Have | Erledigt | `a=rtcp-mux` geparst, serialisiert, in NegotiateAnswer gespiegelt (`RtcpMuxNegotiated`). |
| RFC 5939 | SDP Capability Negotiation | Nice-to-Have | Offen | – |
| RFC 6236 | Negotiation of Generic Image Attributes in SDP | Should Have | Offen | – |
| RFC 7007 | Update to Remove DVI4 from Default MIME Type | Informational | Offen | – |
| RFC 8829 | JSEP (JavaScript Session Establishment Protocol) | Should Have | Offen | – |
| RFC 8843 | Negotiating Media Multiplexing Using SDP (BUNDLE) | Must Have | Erledigt | BUNDLE-Gruppe und MID werden in Offer/Answer korrekt carry-through. |
| RFC 8851 | RTP Payload Format Restrictions (rid) | Nice-to-Have | Offen | – |
| RFC 8853 | Using Simulcast in SDP and RTP | Nice-to-Have | Offen | – |
| RFC 9143 | Negotiating Media Multiplexing Using SDP (BUNDLE Update) | Must Have | Offen | – |

### 4.1 SDP-Sicherheitsattribute

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 4568 | SDP Security Descriptions for Media Streams (SDES) | Should Have | Erledigt | `a=crypto` in `SdpSessionParser`, `SdpCryptoAttribute` Model, Serializer. NegotiateAnswer spiegelt erste Suite (`NegotiatedCrypto`). |
| RFC 5763 | Framework for Establishing a Secure SRTP Connection Using DTLS | Must Have | Erledigt (SDP-Ebene) | `a=fingerprint` (`SdpFingerprint` Model) in Parser, Serializer, Offer/Answer. DTLS-Handshake weiterhin via SIPSorcery. |
| RFC 5764 | DTLS Extension to Establish Keys for SRTP | Must Have | Extern | Key-Austausch via SIPSorcery 10.0.3 |

### 4.2 SDP für ICE

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 8839 | SDP Offer/Answer Procedures for ICE | Must Have | Erledigt (SDP-Ebene) | `a=ice-ufrag`, `a=ice-pwd`, `a=ice-options`, `a=candidate` (via `SdpIceCandidate`), `a=end-of-candidates` in Parser+Serializer; Offer/Answer mit `SdpIceParameters`. ICE-State-Machine fehlt noch. |
| RFC 8840 | A JSEP Usage of SDP for ICE | Should Have | Offen | – |

---

## Teil 5: RTP/RTCP – Real-time Transport Protocol

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3550 | RTP: A Transport Protocol for Real-Time Applications | Must Have | **Erledigt** | Natives RTP-Modul: Paket-Codec (§5.1 + §5.3.1 Extensions + Padding), SSRC-Kollisionserkennung (§8.2), Empfangs-Sequenzprüfung mit Probation (§A.1, MAX_DROPOUT/MAX_MISORDER), UDP-Session. `RtpPacketCodec.cs`, `RtpSession.cs`, `RtpSequenceValidator.cs`. RTCP noch offen. |
| RFC 3551 | RTP Profile for Audio and Video Conferences (AVP) | Must Have | **Erledigt** | Natives AVP-Profil: alle statischen Payload-Typen (PT 0–95) und dynamischer Bereich (PT 96–127) mit Clock-Rates. `RtpAvpProfile.cs`. |
| RFC 3611 | RTP Control Protocol Extended Reports (RTCP XR) | Should Have | Offen | RTCP XR nicht direkt implementiert |
| RFC 4585 | Extended RTP Profile for RTCP-Based Feedback (AVPF) | Must Have | Offen | NACK/PLI/FIR nicht direkt; via SIPSorcery unklar |
| RFC 5104 | Codec Control Messages in the RTP AVPF | Should Have | Offen | FIR/TSTR nicht direkt implementiert |
| RFC 5124 | Extended Secure RTP Profile for RTCP-Based Feedback (SAVPF) | Must Have | Extern | Via SIPSorcery |
| RFC 5285 | A General Mechanism for RTP Header Extensions | Should Have | Offen | One-byte/Two-byte Header Extension nicht direkt |
| RFC 6051 | Rapid Synchronisation of RTP Flows | Should Have | Offen | – |
| RFC 7022 | Guidelines for Choosing RTP Control Protocol CNAMEs | Should Have | Offen | – |
| RFC 7160 | Support for Multiple Clock Rates in an RTP Session | Should Have | Offen | – |
| RFC 7941 | RTP Header Extension for the RTCP Source Description Items | Nice-to-Have | Offen | – |
| RFC 8285 | A General Mechanism for RTP Header Extensions (Two-Byte Update) | Should Have | Offen | – |
| RFC 8852 | RTP Stream Identifier Source Description (RSID) | Nice-to-Have | Offen | – |
| RFC 8888 | RTP Control Protocol Feedback for Congestion Control | Should Have | Offen | – |

---

## Teil 6: SRTP – Secure Real-time Transport Protocol

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3711 | The Secure Real-time Transport Protocol (SRTP) | Must Have | Teilweise | Natives SRTP-Modul: AES-CM-128/256 (§4.1), HMAC-SHA1-80/32 (§4.2), PRF-Schlüsselableitung mit Labels 0x00/0x01/0x02 (§4.3), 64-Paket-Replay-Fenster (§3.3.2), Verify-then-Decrypt, SDES-KeyParam-Parsing. `SrtpContext.cs`, `SrtpKeyDerivation.cs`. Fehlt: AES-GCM (RFC 7714), SRTCP. |
| RFC 4771 | Integrity Transform Carrying Roll-Over Counter | Nice-to-Have | Offen | – |
| RFC 6188 | The Use of AES-192 and AES-256 in Secure RTP | Should Have | **Erledigt** | AES-256-CM via `SrtpCryptoSuite.AesCm256HmacSha1_80/32`; `SrtpKeyDerivation.cs` unterstützt 256-Bit-Keys nativ. |
| RFC 7714 | AES-GCM Authenticated Encryption in SRTP | Must Have | Offen | AES-GCM nicht via SIPSorcery verfügbar; explizit prüfen |
| RFC 5763 | Framework for Establishing a Secure SRTP Connection Using DTLS | Must Have | Extern | Via SIPSorcery |
| RFC 5764 | DTLS Extension to Establish Keys for SRTP (DTLS-SRTP) | Must Have | Extern | use_srtp Extension via SIPSorcery |

### 6.1 DTLS (für DTLS-SRTP)

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 6347 | Datagram Transport Layer Security Version 1.2 (DTLS 1.2) | Must Have | Extern | Via SIPSorcery |
| RFC 9147 | The DTLS Protocol Version 1.3 | Should Have | Offen | DTLS 1.3 nicht in SIPSorcery 10.0.3 |
| RFC 5246 | TLS 1.2 | Must Have | Erledigt | Via `System.Net.Security.SslStream`; `TlsConfiguration.cs` |
| RFC 8446 | TLS 1.3 | Must Have | Erledigt | Via OS/Framework (.NET 8.0 unterstützt TLS 1.3); `TlsConfiguration.cs` |

---

## Teil 7: ICE, STUN, TURN – NAT-Traversal

### 7.1 ICE – Interactive Connectivity Establishment

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 8445 | ICE: A Protocol for NAT Traversal | Must Have | Teilweise | ICE-Attribute implementiert (`IceControlledAttribute.cs`, `IceControllingAttribute.cs`, `UseCandidateAttribute.cs`, `PriorityAttribute.cs`); **ICE-State-Machine fehlt** |
| RFC 8421 | Guidelines for Multihomed and IPv6 ICE | Should Have | Offen | – |
| RFC 8838 | Trickle ICE: Incremental Provisioning of Candidates | Must Have | Offen | – |
| RFC 8839 | SDP Offer/Answer Procedures for ICE | Must Have | Erledigt (SDP-Ebene) | Siehe §4.2. ICE-State-Machine (Connectivity-Checks) fehlt. |
| RFC 8840 | A JSEP Usage of SDP for ICE | Should Have | Offen | – |
| RFC 6544 | TCP Candidates with ICE | Should Have | Offen | – |

### 7.2 STUN – Session Traversal Utilities for NAT

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 8489 | Session Traversal Utilities for NAT (STUN) | Must Have | **Erledigt** | Binding-Request/-Response, HMAC-SHA1 MESSAGE-INTEGRITY, CRC32 FINGERPRINT, Short-/Long-term Credentials, NONCE/REALM, STALE-NONCE (438), Retransmission-Schedule. `StunClient.cs`, `StunServer.cs`, `StunMessageCodec.cs` (36 Dateien) |
| RFC 7635 | STUN Extension for Third-Party Authorization | Nice-to-Have | **Erledigt** | STUN-Attribute `THIRD-PARTY-AUTHORIZATION` (0x802E) + `ACCESS-TOKEN` (0x001B), 401-Third-Party-Challenge inkl. NONCE, sowie A256GCM-basierter Access-Token-Validator mit Timestamp/Lifetime-Checks (`Rfc7635AccessTokenValidator`). |
| RFC 5769 | Test Vectors for STUN | Informational | **Erledigt** | `StunVerificationTests.cs` deckt Test-Vektoren ab |

### 7.3 TURN – Traversal Using Relays around NAT

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 8656 | Traversal Using Relays around NAT (TURN) | Must Have | **Erledigt** | Vollständiger TURN-Kern als isoliertes Modul unter `Infrastructure/Turn/*`: TURN-Client (`Allocate`, `Refresh`, `CreatePermission`, `ChannelBind`, `Send`), TURN-Server über UDP/TCP/TLS (`TurnServer.cs`) inkl. Auth-Challenge (401/438), Allocation-State, Permission- und Channel-Binding-Management sowie Relay-Data-Plane (Peer↔Client via Data-Indication/ChannelData). Testabdeckung in `CalloraVoipSdk.Tests/Turn/*`. |
| RFC 6062 | TURN Extensions for TCP Allocations | Should Have | **Erledigt** | Vollständiger RFC-6062-Flow im isolierten TURN-Modul: TCP-Allocate (`REQUESTED-TRANSPORT=TCP`) inkl. dediziertem TCP-Relay-Listener, `CONNECT`/`CONNECTION-BIND` mit `CONNECTION-ID`, passive eingehende Verbindungen mit `CONNECTION-ATTEMPT`-Indication sowie persistente Client-Data-Connection-API (`OpenTcpDataConnectionAsync`) für Bind+Raw-Relay auf derselben Verbindung. Kernklassen: `TurnAllocateRequestHandler`, `TurnTcpPassiveConnectionService`, `TurnTcpConnectionBroker`, `TurnTcpDataConnectionFactory`. |
| RFC 6156 | TURN Extension for IPv6 | Should Have | **Erledigt** | `REQUESTED-ADDRESS-FAMILY` End-to-End implementiert (`TurnAllocateOptions`, `TurnAttributeMapper`, `TurnServer`): Family-Validierung, 440/443-Fehlerpfade, IPv4/IPv6-Family-Matching in Allocate/Refresh/CreatePermission/ChannelBind/Send. |
| RFC 8016 | Mobility with TURN | Nice-to-Have | **Erledigt** | Mobility-Ticket-Flow für Allocate/Refresh/Migration implementiert (`TurnMobilityService`, `TurnMobilityTicketStore`): Ticket-Ausgabe, Ticket-Refresh, Rebind auf neues Client-Tuple, Cleanup/Invalidation bei Allocation-Removal. |

### 7.4 Veraltete ICE/STUN/TURN-RFCs (Referenz)

| RFC | Titel | Obsoleted by | Hinweis |
|---|---|---|---|
| RFC 5245 | ICE (alt) | RFC 8445 | Nicht neu implementieren |
| RFC 5389 | STUN (alt) | RFC 8489 | Nicht neu implementieren; RFC 8489 verwendet |
| RFC 5766 | TURN (alt) | RFC 8656 | Nicht neu implementieren |
| RFC 3489 | STUN Classic | RFC 5389/8489 | Veraltet; nicht implementieren |

---

## Teil 8: Codecs und RTP-Payload-Formate

### 8.1 Audio-Codecs

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3551 | RTP AVP (G.711 PCMU PT 0 / PCMA PT 8 / G.722 PT 9) | Must Have | **Erledigt** | G.711 (μ-law/a-law) via `G711Codec.cs` (Windows/Linux); G.722 (PT 9) ebenfalls in der Default-Codec-Liste von `SdpUtilities`; PCMU/PCMA/G722 per Default konfiguriert — belegt durch `AudioCodecResolverTests`, `AudioPayloadCodecFactoryTests`, `G722FrameTests`; RTP-Transport via SIPSorcery |
| RFC 3389 | RTP Payload for Comfort Noise (CN, PT 13) | Should Have | Offen | – |
| RFC 4867 | RTP Payload Format for AMR and AMR-WB | Should Have | Offen | – |
| RFC 7587 | RTP Payload Format for Opus | Must Have | Offen | Opus-Codec nicht konfiguriert; fehlt für WebRTC-Interop |
| RFC 4733 | RTP Payload for DTMF Digits (telephone-event) | Must Have | **Erledigt** | RTP telephone-event Sender/Receiver inkl. Domain-Event-Mapping; dynamischer PT via SDP-Parsing (`CallMediaParameters.TelephoneEventPayloadType`); SIP INFO bleibt dokumentierter Fallback |
| RFC 5574 | RTP Payload Format for Speex | Nice-to-Have | Offen | – |
| RFC 3952 | RTP Payload Format for iLBC | Nice-to-Have | Offen | – |

### 8.2 Video-Codecs

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 6184 | RTP Payload Format for H.264 Video | Must Have | Offen | H.264 nicht implementiert |
| RFC 7741 | RTP Payload Format for VP8 Video | Should Have | Offen | VP8 nicht implementiert |
| RFC 7798 | RTP Payload Format for H.265 Video (HEVC) | Should Have | Offen | H.265 nicht implementiert |
| RFC 9328 | RTP Payload Format for VP9 Video | Should Have | Offen | VP9 nicht implementiert |
| RFC 7742 | WebRTC Video Processing and Codec Requirements | Must Have (WebRTC) | Offen | H.264 + VP8 Pflicht für WebRTC |

---

## Teil 9: DNS-Auflösung für SIP

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3263 | Locating SIP Servers | Must Have | **Erledigt** | NAPTR→SRV→A/AAAA; Kandidaten nach priority/weight sortiert; Fehler-Fallback. `SipDnsRouteResolver.cs` via DnsClient 1.8.0 — belegt durch `SipDnsRouteResolverRfc3263Tests` (SRV-Priority ordnet aufsteigend) |
| RFC 2782 | A DNS RR for Specifying the Location of Services (SRV) | Must Have | **Erledigt** | SRV: _sip._udp, _sip._tcp, _sips._tcp; priority/weight. Via DnsClient |
| RFC 3403 | DDDS Part Three: The DNS Database (NAPTR) | Must Have | **Erledigt** | NAPTR-Lookup: E2U+sip, SIP+D2U. Via DnsClient |
| RFC 3596 | DNS Extensions to Support IPv6 (AAAA Records) | Must Have | **Erledigt** | AAAA via DnsClient; IPv4-first-Ordering |
| RFC 6055 | IAB Thoughts on Encodings for Internationalized Domain Names | Informational | Offen | IDN-Handling unklar |

---

## Teil 10: Nummernpläne, Addressing und URI-Formate

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3966 | The tel URI for Telephone Numbers | Must Have | Offen | tel:+49... nicht explizit geparst/gemappt |
| RFC 4694 | Number Portability Parameters for tel URI | Should Have | Offen | – |
| RFC 5341 | The IANA tel URI Parameter Registry | Informational | Offen | – |
| RFC 4458 | SIP URIs for Voicemail and IVR Applications | Nice-to-Have | Offen | – |

---

## Teil 11: Quality of Service und Congestion Control

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 3312 | Integration of Resource Management and SIP | Nice-to-Have | Offen | – |
| RFC 4032 | Update to the SIP Preconditions Framework | Nice-to-Have | Offen | – |
| RFC 4412 | Communications Resource Priority for SIP | Nice-to-Have | Offen | – |
| RFC 8888 | RTP Control Protocol Feedback for Congestion Control | Should Have | Offen | – |
| RFC 5506 | Support for Reduced-Size RTCP | Should Have | Offen | – |

---

## Teil 12: WebRTC-spezifische RFCs

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 7478 | Web Real-Time Communication Use Cases and Requirements | Informational | Offen | – |
| RFC 7742 | WebRTC Video Processing and Codec Requirements | Must Have | Offen | H.264 + VP8 fehlen |
| RFC 7874 | WebRTC Audio Codec and Processing Requirements | Must Have | Offen | Opus fehlt |
| RFC 8825 | Overview: Real-Time Protocols for Browser-Based Applications | Must Have | Offen | – |
| RFC 8826 | Security Considerations for WebRTC | Must Have | Offen | – |
| RFC 8827 | WebRTC Security Architecture | Must Have | Offen | DTLS-SRTP via SIPSorcery; ICE-Auth fehlt |
| RFC 8828 | WebRTC IP Address Handling Requirements | Should Have | Offen | mDNS-Candidates nicht implementiert |
| RFC 8829 | JSEP: JavaScript Session Establishment Protocol | Should Have | Offen | SDP-WebRTC-Attribute fehlen |
| RFC 8832 | The WebRTC Data Channel Establishment Protocol | Nice-to-Have | Offen | SCTP-over-DTLS Data Channels nicht implementiert |
| RFC 8833 | Application Protocol Negotiation for WebRTC Data Channels | Nice-to-Have | Offen | – |
| RFC 8834 | Media Transport and Use of RTP in WebRTC | Must Have | Offen | AVPF+SAVPF, RTCP-MUX, BUNDLE fehlen |
| RFC 8835 | Transports for WebRTC | Must Have | Offen | ICE+DTLS+SRTP Pflicht-Transports nicht vollständig |
| RFC 8836 | Congestion Control Requirements for Interactive Real-Time Media | Should Have | Offen | – |

---

## Teil 13: Weitere relevante Protokoll-RFCs

| RFC | Titel | Priorität | Status SDK | Implementierungsdetail |
|---|---|---|---|---|
| RFC 5234 | Augmented BNF for Syntax Specifications (ABNF) | Must Have | Teilweise | ABNF-Syntax wird größtenteils geparst; bare LF (§7.5 MUST) erledigt in `SipWireProtocol.cs`; keine formale Vollabdeckung |
| RFC 4475 | SIP Torture Test Messages | Must Have | **Erledigt** | `SipRfc4475TortureTests.cs`: 28 Tests grün (CORE-112). §3.1 gültige Nachrichten (LWS, unbekannte Methoden, Escape-Sequenzen, lange Header, Max-Forwards=0, RFC 2543 Via, Contact-Parameter, Wildcard-Contact, folded headers, bare LF) MUST-accept; §3.2 ungültige Nachrichten (negative Content-Length, zu kurzer Body, CSeq-Overflow, Methoden-Mismatch, duplicate To/From) crash-safe reject; §3.3 Crash-Safety (leerer Payload, Binary, Null-Bytes, 65KB+, fehlendes Colon in Header) keine Exception. |
| RFC 5118 | SIP Torture Test Messages for IPv6 | Should Have | Offen | IPv6-spezifische Parser-Fälle offen |
| RFC 4485 | Guidelines for Authors of Extensions to SIP | Informational | Offen | – |

---

## Teil 14: Zusammenfassung Implementierungsstand

### Vollständig erledigt

| Bereich | RFC(s) | Dateien |
|---|---|---|
| SIP Wire Format (§7) | RFC 3261 §7 | `SipWireProtocol.cs`, `SipWireStreamFramer.cs`, `SipHeaderRowRules.cs` |
| SIP UAC §8.1 | RFC 3261 §8.1 | `SipCallSessionTransactionService.cs`, `SipCallSessionHeaderService.cs` |
| SIP Transactions (Client) | RFC 3261 §17.1, RFC 4320, RFC 6026 | `SipClientTransactionExecutor.cs` — Timer A/B/D/E/F/K; Auto-ACK §17.1.1.3; Transport-Error §17.1.4 |
| SIP Transactions (Server) | RFC 3261 §17.2, RFC 4320, RFC 6026 | `SipServerTransactionEngine.cs`, `SipServerTransactionState.cs` — Timer G/H/I/J/L; Transport-Error §17.2.4 |
| DNS/Routing | RFC 3263, RFC 2782, RFC 3403, RFC 3596 | `SipDnsRouteResolver.cs` |
| Multi-Transport + §18 Transport | RFC 3261 §18, RFC 7118 | `SipTransportRuntime.cs`, `SipWebSocketConnection.cs`, `SipProtocol.cs` — §18.1.1 UDP→TCP-Eskalation; §18.2.1 `received=` immer (RFC §18.2.1 MUST); §18.2.2 Via-basiertes Response-Routing; §18.3 Stream-Framing; §18.4 Stale-Connection-Retry |
| TLS 1.2/1.3 | RFC 5246, RFC 8446 | `TlsConfiguration.cs` |
| Digest Auth MD5/SHA-256/SHA-512-256 | RFC 7616, RFC 8760 | `SipDigestAuthentication.cs` |
| Reliable Provisionals (PRACK) | RFC 3262 | `SipReliableProvisionalManager.cs` |
| Session Timers | RFC 4028 | `SipSessionTimerManager.cs`, `SipSessionTimerPolicy.cs` |
| Reason Header | RFC 3326 | `SipReasonHeader.cs` |
| Replaces Header | RFC 3891 | `SipReplacesHeaderValue.cs` |
| STUN Protocol | RFC 8489 | `StunClient.cs`, `StunServer.cs`, `StunMessageCodec.cs` |
| G.711/G.722 Codec | RFC 3551 | `G711Codec.cs`, Default-Codec-Liste in `SdpUtilities` (belegt durch `AudioCodecResolverTests`) |
| P-Asserted-Identity | RFC 3325 | `SipAssertedIdentityHeader.cs`, `ISipIdentityTrustPolicy.cs` |
| RTP Core + Sequenzprüfung | RFC 3550 | `RtpPacketCodec.cs`, `RtpSession.cs`, `RtpSequenceValidator.cs` |
| RTP AVP Profil | RFC 3551 | `RtpAvpProfile.cs` |
| AES-256 für SRTP | RFC 6188 | `SrtpCryptoSuite.cs`, `SrtpKeyDerivation.cs` |
| OPTIONS-Handling | RFC 3261 §11 | `SipCallSignalingService.HandleOptionsAsync` |
| UPDATE-Methode | RFC 3311 | `SipCallSessionTransactionService.cs` |
| REFER-Methode | RFC 3515 | `SipSubscriptionLifecycleManager.cs` |
| Observability | – | `ISipTelemetrySink.cs`, `SipCdrRecord.cs` |
| SIP URI §19 | RFC 3261 §19, RFC 3966 | `SipUriProtocol.cs` — §19.1.2 Percent-Encoding; §19.1.4 RFC-konformer URI-Vergleich (transport-Default, phone-Normalisierung, Header-Vergleich); §19.1.6 tel→SIP-Mapping; §19.2 Option-Tags; §19.3 Tags |
| §20 Header Fields | RFC 3261 §20 | `SipCallSessionHeaderService.cs`, `SipCallSignalingService.cs`, `SipRegistrationService.cs` — §20.37 Supported (100rel,timer,replaces) in allen Requests+Responses; §20.1 Accept in INVITE; §20.11 Content-Disposition:session; §20.35 Server in Responses; §20.17 Date in Responses; §20.33 Retry-After-Parsing aus 503/486/600 |

### Teilweise implementiert (nächste Prioritäten)

| Bereich | RFC(s) | Fehlendes | Priorität |
|---|---|---|---|
| SIP UAS §8.2 | RFC 3261 §8.2 | Vollständige UAS-Compliance | Must Have |
| Dialog-Management | RFC 3261 §12, RFC 6141 | Forking Edge-Cases, re-INVITE vollständig | Must Have |
| SDP Offer/Answer | RFC 8866, RFC 3264 | BUNDLE, RTCP-MUX, ICE-Attribute | Must Have |
| Event Notification | RFC 6665 | **Erledigt**: `NotifyReceived`-Event, `SendNotifyAsync`, `SubscribeAsync` (out-of-dialog). RFC 4662 Resource Lists out-of-scope. | Should Have |
| REFER | RFC 3515, RFC 5589, RFC 3892 | **Erledigt**: REFER senden/empfangen, Referred-By in INVITE/REFER/TransferRequestedEvent, RFC 3892 vollständig | Should Have |
| Outbound | RFC 5626 | **Erledigt**: reg-id=1, +sip.instance, outbound in Supported; flow-token (Server-seitig) out-of-scope | Must Have |
| ICE State Machine | RFC 8445 | Candidate-Gathering, Connectivity-Checks | Must Have |
| STIR/Identity | RFC 8224, RFC 8225, RFC 8226 | PASSporT/JWT-Identity-Header | Should Have |
| Privacy Header | RFC 3323 | **Erledigt**: `SipInviteRequest.Privacy` → `Privacy:`-Header im INVITE | Should Have |
| Opus Codec | RFC 7587 | Opus nicht konfiguriert | Must Have (WebRTC) |
| DTMF RTP | RFC 4733 | **Erledigt**: telephone-event PT via RTP (Sender/Receiver + SIP INFO Fallback) | Must Have |
| SRTP | RFC 3711 | AES-GCM (RFC 7714), SRTCP | Must Have |

### Extern (via SIPSorcery 10.0.3)

| Bereich | RFC(s) | Hinweis |
|---|---|---|
| DTLS 1.2 | RFC 6347 | Key-Aushandlung für DTLS-SRTP |
| DTLS-SRTP | RFC 5763, RFC 5764 | use_srtp Extension; Key-Aushandlung |
| Codecs (Video/Opus) | RFC 6184, RFC 7741, RFC 7587 | H.264/VP8/Opus nicht nativ |

### Bewusst Out-of-Scope (initiale Lieferung)

| Bereich | RFC(s) | Begründung |
|---|---|---|
| Proxy-Funktionalität | RFC 3261 §16 | SDK für User Agent, nicht Proxy |
| S/MIME | RFC 3261 §23 | Veraltet, kein moderner Einsatz |
| MESSAGE-Methode | RFC 3428 | IM out-of-scope |
| Path Header | RFC 3327 | Nur Proxy-relevant |
| GRUU | RFC 5627 | Advanced Registration, optional |
| WebRTC vollständig | RFC 8825–8836 | ICE-SM + BUNDLE + RTCP-MUX fehlen noch |
| AKA Auth | RFC 3310 | IMS/3GPP-spezifisch |

---

## Teil 15: Veraltete RFCs mit Nachfolger

| Veralteter RFC | Titel | Abgelöst durch |
|---|---|---|
| RFC 2327 | SDP (original) | RFC 4566 → RFC 8866 |
| RFC 2543 | SIP (original) | RFC 3261 |
| RFC 2617 | HTTP Digest Auth | RFC 7616 |
| RFC 3265 | SIP Event Notification | RFC 6665 |
| RFC 3489 | STUN classic | RFC 5389 → RFC 8489 |
| RFC 4566 | SDP (2006) | RFC 8866 (2021) |
| RFC 4474 | SIP Identity | RFC 8224 |
| RFC 5245 | ICE | RFC 8445 |
| RFC 5389 | STUN | RFC 8489 |
| RFC 5766 | TURN | RFC 8656 |
| RFC 2234 | ABNF | RFC 5234 |
