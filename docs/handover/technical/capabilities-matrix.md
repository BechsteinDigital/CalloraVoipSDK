# Fähigkeiten- und Reifegrad-Matrix

*Teil des technischen Due-Diligence-Pakets.*
Stand: 2026-07-27 · Code-Basis: `main` / `fix/sip-14-media-hardening` · verifiziert gegen den Code-Graphen (graphify) und die ADRs.

## Zweck und Lesart

Diese Seite ist die **ehrliche Feature-Reifegrad-Matrix** des CalloraVoipSdk. Sie ist bewusst
konservativ formuliert: Sie schützt Käufer und Verkäufer vor Gewährleistungs- und Overclaim-Risiken,
indem sie jede Fähigkeit auf den **im Code und in Tests belegbaren** Stand reduziert.

**Grundregel „Doku ≤ Nachweis":** Wo interne Logs, das TODO-Register oder Marketing-Material
optimistischer sind als der tatsächliche Code, **gewinnt der Code**, und die Einschränkung wird in der
Caveat-Spalte benannt. Jeder „Gebaut & getestet"-Eintrag ist über einen realen Typ im `src/`-Baum und
eine ADR und/oder Testklasse belegt.

**Wichtiger Vorbehalt zur Bedeutung von „getestet":** „Gebaut & getestet" heißt *durch Unit-/
Integrationstests im Repo belegt*. Für den **SIP-/Audio-Kern** kommt ein echter Interop-Nachweis
hinzu: Er ist gegen einen **echten Asterisk** (`andrius/asterisk:22`, eigener CI-Interop-Job,
29 grün inkl. bidirektionaler Zwei-Bein-Media) interop-belegt. **Noch aus stehen** die **Breite**
gegen weitere Referenz-Stacks (FreeSWITCH / 3CX / Fritzbox), ein durchgängiges **Soak-/Chaos-CI-Gate**
sowie **jeglicher WebRTC-/TURN-Nachweis gegen einen realen Stack oder Browser** (siehe
[ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md)). Wo ein produktionskritischer
Interop-Nachweis fehlt oder nur gegen Asterisk erbracht ist, ist das in der Caveat-Spalte
ausdrücklich vermerkt.

### Reifegrad-Stufen

| Stufe | Bedeutung |
|-------|-----------|
| **Gebaut & getestet** | Produktionstyp im `src/`-Baum, durch ADR und/oder Tests im Repo belegt. Kein bekannter Blocker im Kernpfad. |
| **Teilweise** | Kernpfad gebaut und belegt; benannte Corner-Cases, MUST-/SHOULD-Details, Wiring- oder Interop-Lücken offen. |
| **Prototyp / ungetestet** | Baustein oder Endpunkt existiert im Code, ist aber nicht in den Produktionspfad verdrahtet oder nicht durch einen echten End-to-End-/Wire-Test abgesichert. |
| **Nicht gebaut** | Kein Produktionstyp vorhanden — nur Roadmap, ADR-Vorschlag, Marketing-Doku oder Vision. |

---

## 1. SIP-Signaling

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| REGISTER + Digest-Auth (Challenge/Response, Refresh-Lifecycle) | Gebaut & getestet | [ADR-017](../../adr/ADR-017-register-expires-lifecycle.md), [ADR-018](../../adr/ADR-018-challenge-driven-digest-auth.md); `SipRegistrationService`, `ISipDigestAuthenticator` | Interop nur gegen Asterisk belegt (UDP/TCP/TLS-Register grün); Breite gegen andere Registrar fehlt. |
| INVITE-Dialog / Transaktion (UAC + UAS), Retransmission, Fork, 100rel | Gebaut & getestet | [ADR-022](../../adr/ADR-022-invite-transaction-robustness.md); `SipCallSession`, `SipCallSessionTransactionService` | — |
| CANCEL / BYE / ACK / Re-INVITE | Gebaut & getestet | [ADR-005](../../adr/ADR-005-rfc3261-cancel-gate-release.md); `SipCallSessionInboundService`, `SipCallSessionTransactionService` | — |
| Transport UDP / TCP / TLS (SIPS) | Gebaut & getestet | `SipTransportRuntime`, `SipTransportProtocol` | UDP/TCP/TLS-Register + Call gegen Asterisk grün (inkl. NAT-Bridge); TLS-Zert-Ketten-Interop gegen breitere reale Peers nicht separat verifiziert. |
| Session-Timer-Aushandlung (RFC 4028: Interval, 422/Min-SE, Refresher-Rolle) | Teilweise | [ADR-023](../../adr/ADR-023-session-timer-negotiation.md); Parse/Validate/Emit im Code | Aushandlung + Offer-Emission belegt (Known-Answer-Tests, Aushandlung gegen Asterisk grün); der **Refresher-Enforcement-Loop** (aktive Session-Refresh-Timer, der einen Dialog beendet) ist nicht end-to-end belegt. |
| Hold / Unhold, blinder + attended Transfer (REFER) | Gebaut & getestet | [ADR-020](../../adr/ADR-020-dialog-route-set-record-route.md); `SipReferSubscription`, `SipCoreCallChannel.HoldAsync/UnholdAsync/AttendedTransferAsync` | Konferenz/Bridge s. Media. Hold/Unhold + blind/attended Transfer gegen Asterisk grün; Breite der Transfer-Kette gegen andere PBX fehlt. |
| Redirect 3xx (UAS) | Gebaut & getestet | [ADR-002](../../adr/ADR-002-uas-redirect-redirect-async.md); `RedirectAsync` | — |
| Dialog-Route-Set / Record-Route-Echo, In-Dialog-Routing | Gebaut & getestet | [ADR-020](../../adr/ADR-020-dialog-route-set-record-route.md) | — |
| Trunk-Inbound-Matching | Gebaut & getestet | [ADR-021](../../adr/ADR-021-trunk-inbound-matching.md); `TrunkInboundMatcher` | — |
| NAT-routable Contact / Advertised Media Address | Gebaut & getestet | [ADR-019](../../adr/ADR-019-nat-routable-contact-and-media-address.md) | — |
| rport/received-Reflection, CRLF-Keepalive-Pong | Gebaut & getestet | [ADR-003](../../adr/ADR-003-rfc3581-rport-uas-response-reflection.md), [ADR-004](../../adr/ADR-004-rfc5626-crlf-keepalive-pong.md) | — |

## 2. SDP (Offer/Answer)

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| Offer/Answer-Negotiation, Codec-Präferenz, DTMF-Events | Gebaut & getestet | [ADR-024](../../adr/ADR-024-sdp-offer-answer-origin-and-extmap-negotiation.md); `SdpOfferAnswerNegotiator` | — |
| Origin-Versionierung + extmap-Id-Vergabe | Gebaut & getestet | [ADR-024](../../adr/ADR-024-sdp-offer-answer-origin-and-extmap-negotiation.md) | — |
| BUNDLE `a=group:BUNDLE` / mid | Gebaut & getestet | [ADR-010](../../adr/ADR-010-bundle-transport-slice-plan.md), [ADR-011](../../adr/ADR-011-rtp-multitrack-transport.md); `SdpBundleMidInfo` | — |
| Simulcast (`a=simulcast`, `a=rid`) — Sendeseite | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `SdpSimulcast`, `SdpRid`, `SdpOfferAnswerNegotiator.BuildSimulcast` | **Nur Sendeseite.** Empfangsseitiges rid-Demux fehlt — asymmetrische Fähigkeit (siehe Video). |

## 3. Media / RTP / RTCP

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| RTP-Wire-Codec, Send/Receive, Jitter-Buffer, Reorder-Playout | Gebaut & getestet | [ADR-032](../../adr/ADR-032-media-hotpath-allocation-avoidance.md), [ADR-044](../../adr/ADR-044-video-packetisation-and-reorder-playout.md); `RtpPacket`, `VideoReorderBuffer` | — |
| RTCP SR / RR (Sender-/Receiver-Reports), tolerantes Compound-Decode | Gebaut & getestet | [ADR-036](../../adr/ADR-036-rtcp-wire-codec-tolerant-decode.md); `RtcpPacketCodec` | — |
| RTCP-Feedback NACK / PLI / FIR | Gebaut & getestet | [ADR-045](../../adr/ADR-045-video-loss-recovery-nack-pli-rtx.md); `RtcpFeedbackCodec` | — |
| RTX-Retransmission (OSN-Encapsulation, Retransmit-Buffer) | Gebaut & getestet | [ADR-035](../../adr/ADR-035-rtx-retransmission-mechanics.md); `RtxPacketFactory`, `RtpRetransmissionBuffer` | RTX auf der **BUNDLE-Sendeseite** noch nicht als Sekundärstrom verdrahtet (Slice 3–4 offen, s. Congestion/Video). |
| RFC 8285 One-Byte-Header-Extensions, Secondary-Stream-Transport | Gebaut & getestet | [ADR-034](../../adr/ADR-034-secondary-stream-and-onebyte-header-extensions.md); `OneByteRtpHeaderExtensions` | — |
| Media-Hotpath: allocation-arm, lock-free Fan-Out, bounded Drop-Oldest-Buffer | Gebaut & getestet | [ADR-032](../../adr/ADR-032-media-hotpath-allocation-avoidance.md), [ADR-052](../../adr/ADR-052-media-hotpath-lockfree-fanout-and-bounded-buffers.md); `BoundedPlaybackBuffer` | — |
| Monotonic-Clock für zeitbasierte Media/RTCP-Berechnungen | Gebaut & getestet | [ADR-061](../../adr/ADR-061-monotonic-media-clock.md) | Ein bekanntes 2-Zeilen-Dup in `CallRtcpQualityMonitor` als akzeptabel vermerkt (Follow-up, low value). |
| DoS-Hardening an RTP-/SIP-Wire-Grenze, Symmetric-RTP-Latch (CVE-2017-14099) | Gebaut & getestet | [ADR-033](../../adr/ADR-033-wire-boundary-dos-hardening.md); Latch-Hardening `bb5d5c1` | Latch-Reset bei Re-INVITE-Renegotiation als Follow-up registriert. |
| QoS als beobachtete Metrik | Gebaut & getestet | [ADR-053](../../adr/ADR-053-qos-observed-not-marked.md) | Bewusst **beobachtet, nicht DSCP-markiert** — kein aktives Priority-Marking. |

## 4. SRTP / DTLS-Security

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| SRTP AES-CM-Cipher + Schlüsselableitung (RFC 3711) | Gebaut & getestet | [ADR-026](../../adr/ADR-026-srtp-media-path-fail-closed-hardening.md); `AesCmCipher`, `SrtpContext` | — |
| SRTCP-Kontext (Protect/Unprotect, Index, Auth-Tag) | Gebaut & getestet | [ADR-031](../../adr/ADR-031-srtcp-crypto-core-and-rtcp-path-wiring.md); `SrtcpContext` | — |
| SDES-Keying (RFC 4568 inline), Offer/Answer, Fail-Closed keyless-Reject | Gebaut & getestet | [ADR-025](../../adr/ADR-025-sdes-offer-answer-negotiation.md); `SdpCryptoAttribute`, `SrtpKeyMaterial` | — |
| SRTP-Kontinuität über Re-INVITE (Hold/Unhold-Key-Stabilität, Peer-Rekey) | Gebaut & getestet | [ADR-027](../../adr/ADR-027-srtp-continuity-reinvite-rekey.md) | — |
| DTLS-SRTP-Handshake (RFC 5763/5764), Zert-Fingerprint, Keying-Precedence | Gebaut & getestet | [ADR-028](../../adr/ADR-028-dtls-srtp-foundation.md), [ADR-029](../../adr/ADR-029-dtls-srtp-signaling-and-keying-precedence.md), [ADR-030](../../adr/ADR-030-dtls-srtp-media-wiring.md); `DtlsCertificate`, `DtlsSrtpHandshaker` | Kein Browser-Interop-Nachweis des DTLS-Handshakes (s. WebRTC). Per-Context-Key-Zeroing als Follow-up offen. |

## 5. ICE / NAT-Traversal

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| STUN-Binding, srflx-Discovery, Shared-Socket-Gathering | Gebaut & getestet | [ADR-042](../../adr/ADR-042-ice-verification-and-shared-socket-gathering.md); `StunClient`, `StunMessageCodec` | — |
| Send-Side-ICE-State-Machine (SIP-Pfad, RFC 8445) | Gebaut & getestet | [ADR-040](../../adr/ADR-040-send-side-ice-state-machine.md); `CallIceAgent` | — |
| ICE auf dem BUNDLE/WebRTC-Pfad (Consent-Freshness, Nomination-Driver) | Gebaut & getestet | [ADR-010](../../adr/ADR-010-bundle-transport-slice-plan.md); `IceMediaConsentSession`, `IceNominationDriver`, `IceMediaAttachment` | — |
| ICE-Consent-Freshness + ICE-Restart **auf dem SIP-Pfad** | Prototyp / ungetestet | [ADR-041](../../adr/ADR-041-consent-freshness-and-ice-restart-primitives.md); `IceConsentMonitor`, `IceRestartDetector` | **Bausteine gebaut, aber nicht verdrahtet:** kein SIP-Pfad-Caller startet den Consent-Monitor nach Nomination; `IceRestartDetector` wird in Produktion von nichts aufgerufen. RFC-7675-Verhalten für SIP-Calls **nicht** behauptet. Consent-Loss-Reaktion (Terminate vs. Restart) ist als Intent, nicht implementiert. Live nur auf dem BUNDLE/WebRTC-Pfad. |

## 6. TURN-Relay

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| TURN-Client: Allocate / CreatePermission / ChannelBind / Refresh (RFC 5766/8656) | Gebaut & getestet | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md); `TurnClient`, `TurnRelayControlClient` | — |
| TURN-Relay als First-Class-ICE-Kandidat (UDP-Gathering, Send-Path) | Gebaut & getestet | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md); `TurnIceRelayAllocator`, `TurnRelayCandidateSendPath`, `TurnAllocationProbe` | Send-Path byte-identisch zu direct, wenn kein relay injiziert. |
| Allocation-Refresh / Teardown-Keepalive-Loop | Gebaut & getestet | [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md); `TurnAllocationRefreshLoop`, `IRelayKeepAlive` | — |
| Post-Nomination-Whole-Socket-Relay-Transition (ChannelBind → ChannelData) | Prototyp / ungetestet | [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md); `BundledMediaTransport.EnterRelayMode`, `SetRelayChannel` | Transport-Primitive + Orchestrierung einzeln getestet; **kein voller Session-ChannelData-Roundtrip in einem schnellen Test** (Relay-Nominierung über echten Socket ist timeout-gebunden). **Kein Real-Server-E2E, kein Browser-Interop → kein Production-Ready-Claim.** Re-Nomination relay→direct nach Commit ist *geschlossen*, nicht sauber re-transitioniert. |
| Controlled-Agent-Relay (Answerer besitzt den Relay) | Nicht gebaut | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md) „Controlled-agent relay gap" | Nur der **controlling** Agent treibt Nomination + installiert Relay-Permissions. Offerer-relay ↔ Answerer-direct **funktioniert**; Answerer-besitzt-Relay bei direktem Offerer **nicht** — braucht Design, nicht gebaut. |
| TCP/TLS-TURN **Control**-Pfad (Allocate/Refresh) | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `TurnClient` über Stream-Transport | E2E gegen Stream-Transport-`TurnServer` belegt (Control). |
| TCP/TLS-TURN **relay-DATA**-Pfad (Media über Stream-Relay) | Nicht gebaut | SESSION_HANDOFF „großes Feature, Design-Runde nötig" | Media fließt durch **keine** TCP/TLS-TURN-Verbindung. Braucht persistenten Stream-Relay-Transport (ChannelData-Framing + eigener Receive-Loop) parallel zum UDP-Socket. WebRTC-`TurnAllocationProbe` ist UDP-gebunden → TCP/TLS-Relay-Gathering fehlt ganz. |
| RFC 6062 `TurnTcpDataConnection` (öffentliche `ITurnClient`-API) | Teilweise | `TurnTcpDataConnection`, `OpenTcpDataConnectionAsync` | Öffentliche Capability vorhanden, aber **kein VoIP-Media-Use-Case** (relayt TCP-Daten, nicht UDP-Media). |
| Voll-E2E-Relay gegen echten `TurnServer` (Wire-ChannelData-Roundtrip) | Nicht gebaut | SESSION_HANDOFF „4d-6 offen" | Steht aus; jede Slice bisher nur gegen Fake-TURN getestet. |

## 7. Video-Media-Pfad

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| Video-SDP-Negotiation (`m=video`, `rtcp-fb`, RTX/apt) | Gebaut & getestet | [ADR-043](../../adr/ADR-043-video-sdp-negotiation.md); `SdpOfferAnswerNegotiator` | — |
| Video-Packetisierung + Contiguous-Release-Reorder-Playout | Gebaut & getestet | [ADR-044](../../adr/ADR-044-video-packetisation-and-reorder-playout.md); `VideoReorderBuffer`, `Vp8Depacketiser`, `H264Depacketiser`, `AnnexBParser` | **Nur (De-)Packetisierung** — kein Encode/Decode (s. „native Codecs" unten). |
| Video-Loss-Recovery (gated NACK/PLI-Feedback, RTX) | Gebaut & getestet | [ADR-045](../../adr/ADR-045-video-loss-recovery-nack-pli-rtx.md); `VideoKeyFrameFeedback`, `VideoArrivalLossTracker` | Inbound-Loss → Outbound-NACK/PLI gegated auf `a=rtcp-fb` (Slice 1–2). **Video-RTX-Send (Sekundärstrom) noch nicht verdrahtet** (Slice 3–4 offen, design-forked). Leading-edge-Loss NACKt sofort (deferred-tolerance Follow-up). |
| Video-Media-Security (per-m-line SDES, RTX-Keying, DTLS-Precedence) | Gebaut & getestet | [ADR-046](../../adr/ADR-046-video-media-security-sdes-dtls.md) | — |
| Video-ICE (per-5-tuple Media-Layer, geteilte Credentials, volles Gathering) | Gebaut & getestet | [ADR-047](../../adr/ADR-047-video-ice-media-layer-and-candidates.md); `IceMediaAttachment` | — |
| Video-Media-Stream + SIP-Channel-Aktivierung | Gebaut & getestet | [ADR-048](../../adr/ADR-048-video-media-stream-and-channel-activation.md) | — |
| **Nativer VP8/H264-Encode/Decode** | Nicht gebaut | Nur `Vp8Depacketiser`/`H264Depacketiser`/`AnnexBParser` (Transport) | **Codec = transport-only.** SDK packetisiert/depacketisiert VP8- und H.264-**RTP-Payloads**, encodiert/decodiert aber **keine** Video-Frames. Zielbild laut Produkt-Memory bewusst transport-only. |
| **Recv-side Simulcast-Demux** | Nicht gebaut | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); nur Sendeseite (`SdpSimulcast`/`SdpRid`) | Sendeseitiger Simulcast ist gebaut+getestet; **empfangsseitiges rid/Layer-Demux fehlt** — asymmetrische Fähigkeit. |

## 8. Audio / Codecs / Devices

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| G.711 µ-law / A-law Encode/Decode | Gebaut & getestet | [ADR-050](../../adr/ADR-050-bridge-audio-transcoding-opus-mulaw.md); `PcmG711Codec` | — |
| Opus Encode/Decode (managed, via Concentus) | Gebaut & getestet | [ADR-049](../../adr/ADR-049-opus-codec-integration-concentus.md); `OpusPayloadCodec`, `OpusDeviceCodec` | **Managed (kein Hardware-Codec)** — CPU-Ceiling für hohe Kanalzahlen. Tuning (Bitrate, DTX, FEC/PLC) sind Concentus-Defaults, **nicht konfigurierbar**; Opus-PLC/FEC wird bei Loss nicht genutzt. ADR-049: **nicht produktionsbewiesen** (kein Interop-Nachweis). |
| Audio-Device-Capture / Render / Playback (Windows/Linux) | Gebaut & getestet | `PlaybackSession`, `RecordingSession` | Plattform-Abdeckung nur explizit belegte Plattformen; keine breite Geräte-Interop-Matrix. |
| Bridge-Audio-Transcoding (Wire-Codec ↔ µ-law-Tap, Opus↔µ-law/A-law) | Gebaut & getestet | [ADR-050](../../adr/ADR-050-bridge-audio-transcoding-opus-mulaw.md); `BridgeAudioTranscoder` | — |
| Public Per-Call Media-Tap (synchron, encoded, Fan-Out) | Gebaut & getestet | [ADR-059](../../adr/ADR-059-public-media-tap-contract.md) | Synchron/encoded — Consumer-Verhalten (Blockieren) im Fan-Out ist Contract-Grenze. |

## 9. Congestion-Control

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| Transport-Wide-CC Feedback-Plane (seq-Stamping, Arrival-Recording, RTCP-Feedback) | Gebaut & getestet | [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md); `TransportCcFeedbackSender`, timer-driven | — |
| CC-Estimator, AIMD-Rate-Policy, empfohlene-Bitrate-API | Gebaut & getestet | [ADR-039](../../adr/ADR-039-transport-cc-estimator-bitrate-api.md); `CongestionBitrateController`, `TransportCcDelayTrendEstimator` | — |
| Jitter-Buffer-RTT-Convergence-Seed | Gebaut & getestet | [ADR-037](../../adr/ADR-037-rtt-convergence-seed.md) | — |
| **Inbound/Outbound TWCC live im BUNDLE-Pfad** | Prototyp / ungetestet | SESSION_HANDOFF #7 Slice 5–6 (design-forked) | Feedback-Sender + Estimator existieren, aber der `transportWideCcExtensionId` ist im BUNDLE-Pfad **auf `null`** (nicht verdrahtet); die Controller-Platzierung (transport-level) ist ein offener Fork. TWCC-Intervall fix 100 ms (nicht bandbreiten-adaptiv). |

## 10. WebRTC / Browser

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| Öffentliche WebRTC-Fassade (`WebRtcClient`, `IPeerConnection`) | Gebaut & getestet | [ADR-012](../../adr/ADR-012-webrtc-public-facade.md), [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `WebRtcClient`, `WebRtcPeerConnection` | — |
| Fluente ICE-Server-Konfiguration (STUN/TURN) | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `CalloraWebRtcBuilder.WithStunServer/WithTurnServer/WithIceServers` | — |
| Send-Side-Simulcast | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `BundledVideoTrack`-Simulcast | Siehe Video: recv-side Demux fehlt. |
| SDK↔SDK-Peer-Loopback (BUNDLE, DTLS-SRTP, ICE) | Gebaut & getestet | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md); `WebRtcPeerLoopbackTests` | — |
| **Browser-Interop (SDK ↔ echter Browser)** | Nicht gebaut | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) Consequences | **Kein Browser-Interop-Nachweis.** ADR-009 wörtlich: „‚Reif' heißt Code + Tests, **nicht browser-validiert**; die Interop-Validierung bleibt Pflicht vor jedem Produktions-Claim." Bewiesen ist nur SDK↔SDK. |
| **SCTP-DataChannels** | Nicht gebaut | [ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md) „späterer Slice" | Kein Produktionstyp — nur als späterer Roadmap-Slice vermerkt. |

## 11. Server-Hosting (TURN / STUN)

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| STUN-Server (Binding, FINGERPRINT-Validierung) | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `StunServer`, `IStunServerHost` | — |
| TURN-Server (Allocate/Refresh/Permission/ChannelBind, MI, 401/438, RFC 7635, RFC 6156 IPv6-Relay, Lifetime/Quota, RFC 6062 TCP, RFC 8016 Mobility, EVEN-PORT/RESERVATION-TOKEN, DONT-FRAGMENT) | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `TurnServer`, `ITurnServerHost` | **TurnServer beantwortet kein STUN-Binding** (bewusst `default → 400`, verifiziert). Kein Real-World-Betriebsnachweis unter Last. |
| Hosting-Fassade (`AddCalloraTurnServer` / `AddCalloraStunServer`) | Gebaut & getestet | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md); `CalloraVoipSdk.Hosting` | Nicht als Alternate-Server/300-Redirect-Cluster (Clustering) gebaut; RFC 5780 NAT-Behavior-Discovery nicht gebaut (bewusst Scope-out). |

## 12. Öffentliche API / Facade

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| `VoipClient` / `IVoipClient` (zentrale Runtime-Facade) | Gebaut & getestet | [ADR-006](../../adr/ADR-006-api-versioning-strategy.md); `VoipClient`, `IVoipClient` | — |
| DDD-Layering, gated shrink-only Baselines (Arch-Tests) | Gebaut & getestet | [ADR-014](../../adr/ADR-014-ddd-layering-gated-baselines.md); `EngineeringRulesTests` | — |
| `ICallRegistry` Domain-Port (DIP) | Gebaut & getestet | [ADR-015](../../adr/ADR-015-icallregistry-domain-port-dip.md) | — |
| API-Versionierung / Kompatibilitätsstrategie | Gebaut & getestet | [ADR-006](../../adr/ADR-006-api-versioning-strategy.md) | ADR-006 §4 trägt eine dokumentierte Errata (API-Surface-Gate-Prosa wich vom Code ab). |
| `ModuleRegistry` (Extensibilitäts-Seam für Plugins) | Gebaut & getestet | [ADR-007](../../adr/ADR-007-host-centric-platform-split.md); `ModuleRegistry` | Generischer Registry-Seam — **kein** konkretes Differenzierungsmodul (s. §13). |
| Inbound Custom-INVITE-Header über `ICall` | Nicht gebaut | Interne Notiz „Inbound Custom-Header-Gap" | `IncomingCall`/`ICall` exponiert keine Custom-INVITE-Header (z. B. Contact-Center-Prefetch per Custom-Key). PO-Kandidat, offen. |

## 13. Differenzierungsmodule (Privacy / Risk / Intelligence / Policy)

| Fähigkeit | Reifegrad | Beleg (ADR / Code) | Einschränkung / Caveat |
|-----------|-----------|--------------------|------------------------|
| `CalloraVoipSdk.Privacy` (Redaction, Consent, Policy-Gates, Audit) | Nicht gebaut | CEO_VISION (intern, nicht Teil des Pakets) Phase 3; nur `docs/portal/commercial/privacy.md` | **Vision, kein Produktionstyp.** Kein `src/`-Projekt. Einzige gebaute Bausteine mit Namensnähe sind generisch (z. B. `SipWireTraceRedactionTests` = Trace-Redaction im Logging-Pfad, ICE-Consent-Freshness), **kein Privacy-Modul**. |
| `CalloraVoipSdk.Risk` (Spam-/Scam-Signale, Call-Risk-Screening) | Nicht gebaut | CEO_VISION (intern, nicht Teil des Pakets) Phase 3; nur `docs/portal/commercial/risk.md` | **Vision, kein Produktionstyp.** Kein `src/`-Projekt. |
| `CalloraVoipSdk.Intelligence` (AMD, Sentiment, Transcript, lokale Modelle) | Nicht gebaut | CEO_VISION (intern, nicht Teil des Pakets) Phase 3; nur `docs/portal/commercial/intelligence.md` | **Vision, kein Produktionstyp.** Kein `src/`-Projekt. Media-Tap ([ADR-059](../../adr/ADR-059-public-media-tap-contract.md)) ist der vorgesehene Integrationspunkt, das Modul selbst ist nicht gebaut. |
| `CalloraVoipSdk.Policy` (Tenant-Regeln, Decision-Profiles, Compliance) | Nicht gebaut | CEO_VISION (intern, nicht Teil des Pakets) Phase 3; nur `docs/portal/commercial/*.md` | **Vision, kein Produktionstyp.** Kein `src/`-Projekt. |

---

## Zusammenfassung der ehrlich markierten Kernlücken

Diese Punkte sind für die Due Diligence besonders relevant und in der Matrix oben belegt:

1. **Kein Browser-Interop-Nachweis.** WebRTC ist SDK↔SDK bewiesen (Loopback + BUNDLE + DTLS-SRTP), aber nie gegen einen echten Browser validiert ([ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md)). Kein Produktions-Claim.
2. **Native Video-Codecs = transport-only.** VP8/H.264 werden (de-)packetisiert, aber nicht encodiert/decodiert.
3. **Recv-side Simulcast-Demux fehlt** — Simulcast ist nur sendeseitig.
4. **TURN-Relay-Datenpfad ist Prototyp-Stufe:** Post-Nomination-Whole-Socket-Transition ist gebaut, aber ohne Real-Server-E2E; **Controlled-Agent-Relay-Gap** (Answerer-besitzt-Relay nicht gebaut); **kein TCP/TLS-Relay-Datenpfad**.
5. **ICE-Consent-Freshness/Restart auf dem SIP-Pfad = unverdrahtete Primitive** — RFC-7675-Verhalten wird für SIP-Calls nicht behauptet (nur BUNDLE/WebRTC-Pfad live).
6. **Inbound/Outbound TWCC im BUNDLE-Pfad nicht verdrahtet** (`transportWideCcExtensionId == null`); Feedback/Estimator existieren.
7. **Differenzierungsmodule (Privacy/Risk/Intelligence/Policy) sind Vision, nicht gebaut** — kein `src/`-Projekt, nur Portal-/Marketing-Doku.
8. **Opus ist managed und nicht produktionsbewiesen**; Tuning nicht konfigurierbar, PLC/FEC bei Loss ungenutzt.
9. **Session-Timer-Refresher-Enforcement-Loop** nur teilweise (Aushandlung belegt, aktiver Refresh-Timer nicht end-to-end).
10. **Interop-/Soak-Nachweis teilweise:** Der **SIP-/Audio-Kern ist gegen echten Asterisk** (`andrius/asterisk:22`, eigener CI-Interop-Job, 29 grün inkl. bidirektionaler Zwei-Bein-Media) interop-belegt. **Fehlend:** die **Breite** gegen weitere Stacks (FreeSWITCH/3CX/Fritzbox), ein durchgängiges **Soak-/Chaos-CI-Gate**, sowie **WebRTC/TURN gegen jeden realen Stack/Browser** (ungetestet) ([ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md)).

*Bezugsdokumente: [ADR-Index](../../adr/README.md); interne Status-Roh-Register (SDK_COMPLETION_TODO, SESSION_HANDOFF) und CEO_VISION sind intern und nicht Teil des Pakets (auf Anfrage/NDA). Verifikationsmethode: Code-Graph-Abfrage (graphify) je Fähigkeit + ADR-Consequences/Guardrails.*
