# Decision Inventory — Agent-Logs → ADR-Cluster

Dieses Inventar ordnet jeden der 113 chronologischen Agent-Logs unter `docs/agent-log/`
GENAU EINEM Themencluster zu. Es ist die Grundlage der späteren ADR-Gewinnung: aus jedem
Cluster werden ADRs destilliert, dieses Dokument liefert das Rückgrat (welcher Log gehört
zu welcher Entscheidung). Die Spalte „vermutete Kernentscheidung" ist bewusst knapp und
teils vermutend (aus Dateiname/Kontext); die inhaltliche Tiefe kommt im ADR-Schritt.
Stand: 2026-07-27.

## Zuordnungstabelle

| Cluster | Log-Datei | vermutete Kernentscheidung (kurz) | Ziel-ADR-Cluster |
|---|---|---|---|
| C01 | 2026-04-14-ceo.md | Rollen/Priorisierung: CEO-Entscheidungslauf | C01 DDD-/Modul-Architektur & Rollen |
| C01 | 2026-04-14-dev.md | Rollen-Workflow: DEV-Lauf im rollenbasierten Prozess | C01 DDD-/Modul-Architektur & Rollen |
| C01 | 2026-04-14-po.md | Rollen: PO-Scope-/Akzeptanz-Schnitt | C01 DDD-/Modul-Architektur & Rollen |
| C01 | 2026-04-14-reviewer.md | Rollen: Reviewer-Scope-/Claim-Prüfung | C01 DDD-/Modul-Architektur & Rollen |
| C18 | 2026-07-07-dev-a2.md | Früh-Build-Slice A2 (SDK-Entschlackung) | C18 Früh-Build-Slices |
| C18 | 2026-07-07-dev-a3.md | Früh-Build-Slice A3 | C18 Früh-Build-Slices |
| C18 | 2026-07-07-dev-b1-1.md | Früh-Build-Slice B1-1 | C18 Früh-Build-Slices |
| C18 | 2026-07-07-dev-b1.md | Früh-Build-Slice B1 | C18 Früh-Build-Slices |
| C18 | 2026-07-07-dev-c1.md | Früh-Build-Slice C1 | C18 Früh-Build-Slices |
| C01 | 2026-07-07-dev.md | A1: Entfernung nicht implementierter Modul-Oberflächen aus SDK-Facade (Architektur) | C01 DDD-/Modul-Architektur & Rollen |
| C17 | 2026-07-07-true-up.md | STATE/Claim True-up der A/B/C-Slices | C17 Audit/Code-Review/True-up |
| C17 | 2026-07-08-audit.md | Delta-Audit: Vorreview K1/K2 übergemeldet (SrtpContext thread-safe, Keys genullt) | C17 Audit/Code-Review/True-up |
| C01 | 2026-07-08-dev-b3-layer-hygiene.md | Layer-Hygiene: keine Schichtverletzungen (DDD) | C01 DDD-/Modul-Architektur & Rollen |
| C14 | 2026-07-08-dev-b4-threading.md | Threading/Thread-Safety by design | C14 Threading & Memory-Safety |
| C03 | 2026-07-08-dev-b5-sdp-origin-version.md | SDP o=-Zeile: Origin/Version-Handhabung | C03 SDP-Aushandlung |
| C13 | 2026-07-08-dev-codec-opus-bridge.md | Opus-Codec-Bridge (Transport-only) | C13 Codec/Opus |
| C13 | 2026-07-08-dev-codec-opus.md | Opus-Codec-Integration (Concentus, Transport-only) | C13 Codec/Opus |
| C11 | 2026-07-08-dev-ice-verification.md | ICE-Verifikation | C11 ICE |
| C02 | 2026-07-08-dev-m1-hotfix.md | M1-Hotfix Media-Pfad: SDP c=-Adresse (NAT-Resolve) + Codec-Wahl | C02 SIP-Signaling |
| C01 | 2026-07-08-dev-phoneline-callmanager-decouple.md | PhoneLine/CallManager entkoppeln (Architektur) | C01 DDD-/Modul-Architektur & Rollen |
| C15 | 2026-07-08-dev-qos.md | QoS/DSCP-Markierung Media | C15 QoS |
| C02 | 2026-07-08-dev-sip-media-nat.md | SIP Media-NAT-Resolve (advertised media address) | C02 SIP-Signaling |
| C02 | 2026-07-08-dev-sip-public-contact.md | SIP Public-Contact-Adresse | C02 SIP-Signaling |
| C02 | 2026-07-08-dev-sip-record-route-response.md | SIP Record-Route in Responses | C02 SIP-Signaling |
| C02 | 2026-07-08-dev-sip-registration-expires.md | SIP REGISTER Expires-Handhabung | C02 SIP-Signaling |
| C02 | 2026-07-08-dev-sip-rport-contact.md | SIP rport/Contact (NAT) | C02 SIP-Signaling |
| C02 | 2026-07-08-dev-sip-trunk-inbound.md | SIP Trunk-Inbound-Handling | C02 SIP-Signaling |
| C04 | 2026-07-08-dev-srtp-e2e.md | SRTP End-to-End-Media | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-08-dev-srtp-hardening.md | SRTP-Hardening (Per-SSRC replay/ROC) | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-08-dev-srtp-media-path.md | SRTP Media-Pfad-Verdrahtung | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-08-dev-srtp-s0-step1.md | SRTP Fundament S0-Step1 | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-08-dev-srtp-sdes-answer.md | SDES-Keying im SDP-Answer | C04 SRTP/SDES Media-Security |
| C17 | 2026-07-08-full-sdk-code-review.md | Voll-Code-Review des SDK (Basis für Delta-Audit) | C17 Audit/Code-Review/True-up |
| C02 | 2026-07-09-analysis-b7-expires-in-responses.md | Analyse B7: Expires in Responses (SIP) | C02 SIP-Signaling |
| C07 | 2026-07-09-dev-b6-rtp-receive-pooling.md | RTP-Receive-Buffer-Pooling (RAM-effizient) | C07 RTP-Transport & Hardening |
| C07 | 2026-07-09-dev-b6-sip-framer-alloc.md | SIP-Framer-Allokation (RTP/Transport-Hardening) | C07 RTP-Transport & Hardening |
| C02 | 2026-07-09-dev-b7-double-invite-retransmission.md | SIP Double-INVITE-Retransmission | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b7-fork-error-warning.md | SIP Fork-Error-Warning | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b7-password-only-on-register.md | SIP Passwort nur bei REGISTER | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b8-dialog-route-header-tests.md | SIP Dialog Route-Header-Tests | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b8-digest-auth-tests.md | SIP Digest-Auth-Tests | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b8-register-digest-retry.md | SIP REGISTER Digest-Retry | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b8-session-timer-tests.md | SIP Session-Timer-Tests | C02 SIP-Signaling |
| C02 | 2026-07-09-dev-b8-uac-route-set-tests.md | SIP UAC Route-Set-Tests | C02 SIP-Signaling |
| C09 | 2026-07-09-dev-b9-rtcp-xr.md | RTCP-XR (Extended Reports) | C09 RTCP/Feedback/XR/RTT |
| C09 | 2026-07-09-dev-b9-rtt-seed.md | RTT-Seed (monotone Uhr) | C09 RTCP/Feedback/XR/RTT |
| C11 | 2026-07-09-dev-ice-i1-checklist-foundation.md | ICE I1: Checklist-Foundation | C11 ICE |
| C11 | 2026-07-09-dev-ice-i2b-wire-attributes.md | ICE I2b: Wire-Attribute | C11 ICE |
| C11 | 2026-07-09-dev-ice-i3-fsm-connectivity.md | ICE I3: FSM Connectivity-Checks | C11 ICE |
| C11 | 2026-07-09-dev-ice-i4-nomination.md | ICE I4: Checked-Nomination (RFC 8445) | C11 ICE |
| C11 | 2026-07-09-dev-ice-i6-consent-freshness.md | ICE I6: Consent-Freshness | C11 ICE |
| C11 | 2026-07-09-dev-ice-i8-restart.md | ICE I8: Restart | C11 ICE |
| C04 | 2026-07-09-dev-peer-rekey-slice1.md | SRTP Peer-Rekey Slice1 | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-09-dev-peer-rekey-slice2.md | SRTP Peer-Rekey Slice2 | C04 SRTP/SDES Media-Security |
| C07 | 2026-07-09-dev-rtp-short-packet-dos.md | RTP Short-Packet-DoS-Härtung | C07 RTP-Transport & Hardening |
| C06 | 2026-07-09-dev-srtcp-crypto-core.md | SRTCP Crypto-Core | C06 SRTCP |
| C06 | 2026-07-09-dev-srtcp-wiring.md | SRTCP-Verdrahtung | C06 SRTCP |
| C04 | 2026-07-09-dev-srtp-holdunhold-continuity.md | SRTP Hold/Unhold-Kontinuität | C04 SRTP/SDES Media-Security |
| C04 | 2026-07-09-dev-srtp-offer-sdes.md | SDES-Keying im SDP-Offer | C04 SRTP/SDES Media-Security |
| C05 | 2026-07-14-dev-dtls-media-wiring.md | DTLS-SRTP Media-Wiring | C05 DTLS-SRTP |
| C05 | 2026-07-14-dev-dtls-signaling.md | DTLS-SRTP Signaling (fingerprint/setup) | C05 DTLS-SRTP |
| C05 | 2026-07-14-dev-dtls-srtp-foundation.md | DTLS-SRTP Fundament (exporter-secret wipe) | C05 DTLS-SRTP |
| C09 | 2026-07-14-dev-rtcp-feedback-wire.md | RTCP-Feedback Wire-Format | C09 RTCP/Feedback/XR/RTT |
| C07 | 2026-07-14-dev-rtp-onebyte-header-extensions.md | RTP One-Byte-Header-Extensions | C07 RTP-Transport & Hardening |
| C07 | 2026-07-14-dev-rtpsession-secondary-stream.md | RtpSession Secondary-Stream | C07 RTP-Transport & Hardening |
| C08 | 2026-07-14-dev-rtx-mechanics.md | RTX-Retransmission-Mechanik | C08 RTX-Retransmission |
| C03 | 2026-07-14-dev-sdp-extmap-negotiation.md | SDP extmap-Aushandlung | C03 SDP-Aushandlung |
| C12 | 2026-07-14-dev-video-channel-activation.md | Video-Channel-Activation | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-contiguous-playout.md | Video Contiguous-Playout | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-dtls-sdes-precedence.md | Video DTLS/SDES-Keying-Precedence | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-ice-candidates.md | Video ICE-Candidates | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-ice-media-layer.md | Video ICE-Media-Layer | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-ice-srflx-relay.md | Video ICE srflx/relay-Gathering | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-media-stream.md | Video-Media-Stream | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-nack-gating.md | Video NACK-Gating | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-packetisation.md | Video-Packetisation | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-pli-feedback.md | Video PLI-Feedback | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-reorder-buffer.md | Video Reorder-Buffer | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-reorder-loss-signalling.md | Video Reorder-/Loss-Signalling | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-rtcp-fb-sdp.md | Video RTCP-FB im SDP | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-rtx-receive.md | Video RTX-Receive | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-rtx-retransmit.md | Video RTX-Retransmit | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-rtx-sdp.md | Video RTX im SDP | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-sdes-keying.md | Video SDES-Keying | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-sdes-offer.md | Video SDES im Offer | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-sdes-rtx-keying.md | Video SDES/RTX-Keying | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-sdes-sdp-answer.md | Video SDES im SDP-Answer | C12 Video-Medienpfad |
| C12 | 2026-07-14-dev-video-sdp.md | Video-SDP (m=video Aushandlung) | C12 Video-Medienpfad |
| C09 | 2026-07-15-dev-rtcp-tolerant-feedback-decode.md | RTCP tolerant Feedback-Decode | C09 RTCP/Feedback/XR/RTT |
| C10 | 2026-07-15-dev-transport-cc-arrival-recorder.md | Transport-CC Arrival-Recorder | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-congestion-wiring.md | Transport-CC Congestion-Wiring | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-delay-signal.md | Transport-CC Delay-Signal | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-delay-trend.md | Transport-CC Delay-Trend | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-feedback-builder.md | Transport-CC Feedback-Builder | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-feedback-interpreter.md | Transport-CC Feedback-Interpreter | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-feedback.md | Transport-CC Feedback (Kern) | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-feedback-sender.md | Transport-CC Feedback-Sender | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-rtcp-dispatch.md | Transport-CC RTCP-Dispatch | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-cc-sender-findings.md | Transport-CC Sender-Findings | C10 Transport-Wide Congestion Control |
| C10 | 2026-07-15-dev-transport-wide-cc-seq.md | Transport-Wide-CC Sequence-Number-Extension | C10 Transport-Wide Congestion Control |
| C17 | 2026-07-16-audit.md | Voll-Audit 2026-07-16 (Pakete A–I, GA-Scope) | C17 Audit/Code-Review/True-up |
| C01 | 2026-07-17-refactoring-assessment.md | Refactoring-Assessment (Struktur/Extract-Class) | C01 DDD-/Modul-Architektur & Rollen |
| C16 | 2026-07-19-dev.md | TURN-Relay 4d-2b: media-socket-bound Gathering | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4c-4a.md | TURN Allocation-Refresh/Teardown-Keepalive-Loop | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4c-4b.md | TURN-Keepalive in Session-Lifecycle verdrahtet | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-1.md | Relay-fähiger ICE-Agent (Fork B, 4d-3b-1) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-2a.md | Transport-Inbound-Routing Relay-Indication (4d-3b-2a) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-2b-ii-A.md | Rtp-seitiger Relay-Binding-Seam (4d-3b-2b-ii-A) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-2b-ii-B.md | WebRtc Relay-Binding-Producer, Offerer-Pfad (ii-B) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-2b-i.md | Outbound-Engine TurnRelayCandidateSendPath (2b-i) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-3b-2c.md | Answerer Late-Local-Candidate-Adoption (2c) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-4c-ii.md | Relay-Pair-Nominierung → ChannelData (Fork A, 4c-ii) | C16 TURN-Relay |
| C16 | 2026-07-19-dev-relay-4d-4c-i.md | Transport-Primitive EnterRelayMode (Fork A, 4c-i) | C16 TURN-Relay |

## Cluster-Übersicht

| Cluster-ID | Name | Anzahl Logs |
|---|---|---|
| C01 | DDD-/Modul-Architektur & Rollen | 8 |
| C02 | SIP-Signaling | 15 |
| C03 | SDP-Aushandlung | 2 |
| C04 | SRTP/SDES Media-Security | 9 |
| C05 | DTLS-SRTP | 3 |
| C06 | SRTCP | 2 |
| C07 | RTP-Transport & Hardening | 5 |
| C08 | RTX-Retransmission | 1 |
| C09 | RTCP/Feedback/XR/RTT | 4 |
| C10 | Transport-Wide Congestion Control | 11 |
| C11 | ICE | 7 |
| C12 | Video-Medienpfad | 20 |
| C13 | Codec/Opus | 2 |
| C14 | Threading & Memory-Safety | 1 |
| C15 | QoS | 1 |
| C16 | TURN-Relay | 11 |
| C17 | Audit/Code-Review/True-up | 4 |
| C18 | Früh-Build-Slices | 5 |
| **Summe** | | **113** |
