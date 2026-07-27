# Protokoll-Konformität — RFC-Übersicht

*Teil des technischen Due-Diligence-Pakets.*
Stand: 2026-07-27 · Code-Basis: `main` (Media-/SIP-Kern), verifiziert gegen den Code-Graphen.

## Zweck und Lesart

Diese Seite verdichtet den RFC-Konformitätsstand des CalloraVoipSdk auf einen belastbaren,
käuferseitig prüfbaren Kern. Sie ersetzt **nicht** die Roh-Register, sondern fasst sie zusammen
und korrigiert bekannte Overclaims.

**Grundregel „Doku ≤ Nachweis":** Wo die interne Compliance-Referenz mehr behauptet, als Code
oder ADR belegen, ist der Eintrag hier auf den belegbaren Stand reduziert und in der Anmerkung
gekennzeichnet. Abdeckungsstufen:

| Stufe | Bedeutung |
|-------|-----------|
| **Voll** | Wire-korrekt im Code umgesetzt und durch ADR/Tests belegt. Kein bekannter MUST-Gap. |
| **Teilweise** | Kernpfad umgesetzt und belegt; benannte Corner-Cases / MUST- oder SHOULD-Details offen. |
| **Nicht** | Nicht oder nur als Stub vorhanden. |

**Wichtiger Vorbehalt:** „Voll/Teilweise" bedeutet *im Code belegt*, **nicht** automatisch *durch
Interop-Test gegen einen Referenz-Stack verifiziert*. Für den **SIP-/Audio-Kern** existiert eine
reale Interop-/Soak-Suite gegen **zwei echte SIP-Stacks** — **Asterisk** (im PR-CI-Gate) **und
FreeSWITCH** (lokal-first, gleiche `IPbxFixture`-Szenario-Matrix) — siehe
[ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md). Für den **WebRTC-/TURN-/Browser-Pfad**
gibt es seit 2026-07-27 einen **ersten realen Interop-Nachweis** — der TURN-Relay-Datenpfad gegen einen
echten in-Process-`TurnServer` (Loopback) und der WebRTC-Kern gegen einen echten Chrome (Connect +
bidir. Audio, lokal-first; siehe [`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md)).
Ein **umfassender** Interop-Nachweis (Video-Browser-Interop, Browser-Offerer-Richtung, externer coturn)
steht weiterhin **aus**.

**Primärquellen dieser Verdichtung:**
- ADR-Verzeichnis (getrackter, code-verifizierter Beleg): [`../../adr/README.md`](../../adr/README.md)
- Code-belegtes RFC-Konformitäts-Gap-Register (Datei:Zeile, gesamter Infrastructure-Baum) — intern archiviert, nicht Teil des Pakets (auf Anfrage/NDA).
- SIP-Kapitelstatus (RFC 3261, konservativ) — intern archiviert, nicht Teil des Pakets (auf Anfrage/NDA).

---

## 1. SIP-Signaling

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 3261** — SIP Core | UA (Client + Server), Transaktions-Layer, Wire-Codec, Dialoge, URI | **Teilweise** | Transaktions-Engine (alle Timer A/B/D/E/F/G/H/I/J/K/L), Wire-Codec §7/§20; [ADR-020](../../adr/ADR-020-dialog-route-set-record-route.md), [ADR-022](../../adr/ADR-022-invite-transaction-robustness.md) | Kern-Callflows (INVITE/ACK/BYE/CANCEL/OPTIONS/re-INVITE) belegt. Offen: In-Dialog-Matching nur per Call-ID ohne vollen Tag-Abgleich (§12.2, im 2-Party-UA-Fall begrenzt); Strict-Router-Rewrite (§16.12) fehlt (Loose-Routing-Standardfall vorhanden). **Keine Proxy-Rolle (§16, bewusst out-of-scope).** |
| **RFC 3581** — Symmetric Response Routing (`rport`) | Via `rport`/`received` in UAS-Responses | **Teilweise** | [ADR-003](../../adr/ADR-003-rfc3581-rport-uas-response-reflection.md); `SipProtocol.ReflectViaParameters` | Reflektion im Signaling-Pfad belegt. Anmerkung: kein Write-Back im generischen Transaction-Layer (UAS-Antworten über die Engine lernen NAT-Contact nicht). |
| **RFC 5626** — Managing Client-Initiated Connections (Outbound) | CRLF-Keepalive; reg-id / `+sip.instance` | **Teilweise** | [ADR-004](../../adr/ADR-004-rfc5626-crlf-keepalive-pong.md); `SipStreamConnection` (CRLF-Pong), `SipRegistrationService` (reg-id) | CRLF-Keepalive-Pong (§4.4.1) und reg-id/instance-id belegt. Offen: server-seitige Flow-Token / Multi-Flow. |
| **RFC 3262** — Reliable Provisional Responses (100rel / PRACK) | PRACK, RSeq/RAck, 1xx-Retransmit | **Teilweise** | [ADR-022](../../adr/ADR-022-invite-transaction-robustness.md); `SipReliableProvisionalManager` | Sende-/Empfangspfad + 1xx-Retransmit belegt. Offen: empfängerseitige RSeq-Monotonie-/Duplikat-PRACK-Validierung. |
| **RFC 4028** — Session Timers | Session-Expires / Min-SE / refresher / 422 | **Teilweise** | [ADR-023](../../adr/ADR-023-session-timer-negotiation.md); `SipSessionTimerPolicy` | Aushandlung + Refresh + 422-Retry belegt. Offen: inbound Peer-`Min-SE` > Angebot hebt nicht an; refresher-Renegotiation-Edgecases. |
| **RFC 7616 / RFC 2617** — HTTP Digest (SIP §22) | MD5, MD5-sess, SHA-256(-sess), SHA-512-256(-sess), qop=auth/auth-int | **Teilweise** | [ADR-018](../../adr/ADR-018-challenge-driven-digest-auth.md); `SipDigestAuthentication.TryResolveAlgorithm`, `ProtocolCommonUtilities` (SHA-512/256 via BouncyCastle) | **Korrektur zum internen Register:** SHA-512-256 wird tatsächlich berechnet und akzeptiert (Fix-Commit `8ffc08a`); der im Register (2026-07-19) gelistete „SHA-512-256-Deadlock" (S1) ist **behoben und veraltet**. qop=auth-int seit `d6619d5`. Offen: `nc`-Reset bei neuer nonce; SUBSCRIBE umgeht den Stärkste-Algorithmus-Selector. |
| **RFC 8760** — Digest Auth Strength | Algorithmus-Priorisierung (stärkste Challenge) | **Teilweise** | `SipDigestChallengeSelector`; `SipDigestAuthentication` | Selector wählt stärkste Challenge, Authenticator unterstützt sie inkl. SHA-512-256. Offen: dedizierte RFC-8760-Interop-Randfälle. |

---

## 2. SDP & Offer/Answer

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 4566 / RFC 8866** — SDP | Grundstruktur, Parse/Serialize (IPv4+IPv6), m-line-Order | **Teilweise** | [ADR-024](../../adr/ADR-024-sdp-offer-answer-origin-and-extmap-negotiation.md); `SdpSessionParser`, `SdpSessionSerializer` | Kern belegt. Offen: `a=ssrc`/`a=ssrc-group` (RFC 5576) fehlt komplett; `m=`-Port-Range → FormatException; `bundle-only` fehlt. |
| **RFC 3264** — Offer/Answer mit SDP | Codec-Intersection, Direction-Inversion, Port-0-Reject, Hold/Unhold | **Teilweise** | [ADR-024](../../adr/ADR-024-sdp-offer-answer-origin-and-extmap-negotiation.md); `SdpOfferAnswerNegotiator.NegotiateAnswer` | Kern belegt (inkl. telephone-event/RFC 4733 im SDP). Offen: `o=`-SessionVersion wird bei Re-Offer nicht inkrementiert (`?? 0`) — JSEP/Reoffer-relevant. |

---

## 3. RTP / RTCP & Media-Extensions

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 3550 / 3551** — RTP/RTCP | Header, Seq/TS/SSRC-Random + Wraparound, Seq-Validierung §A.1, SR/RR/SDES-CNAME, PLI/NACK/FIR | **Teilweise** | [ADR-034](../../adr/ADR-034-secondary-stream-and-onebyte-header-extensions.md), [ADR-036](../../adr/ADR-036-rtcp-wire-codec-tolerant-decode.md), [ADR-061](../../adr/ADR-061-monotonic-media-clock.md); `RtpSession`, `RtcpWireCodec` | SIP-Pfad solide. Offen (MUST): SSRC-Kollision wird erkannt+geloggt, aber ohne neues SSRC+BYE+Reseed (§8.2); RTCP-Intervall fix 5 s statt bandbreitenproportional+randomisiert (§6.2); CNAME nicht session-unique; kein BYE-Send bei Session-Ende. |
| **RFC 8285** — RTP Header Extensions (One-Byte) | extmap, MID/RID-Ext | **Voll** | [ADR-034](../../adr/ADR-034-secondary-stream-and-onebyte-header-extensions.md); `OneByteRtpHeaderExtensions` | One-Byte-Form belegt; kein bekannter Gap. |
| **RFC 4588** — RTP Retransmission (RTX) | OSN-Encapsulation, apt-Bindung, Retransmit-Buffer | **Voll** | [ADR-035](../../adr/ADR-035-rtx-retransmission-mechanics.md) | Belegt (Send-Seite + apt-Negotiation). |
| **RFC 4585** — RTP/AVPF (RTCP-FB) | PLI/NACK/FIR-Feedback | **Teilweise** | [ADR-045](../../adr/ADR-045-video-loss-recovery-nack-pli-rtx.md), [ADR-036](../../adr/ADR-036-rtcp-wire-codec-tolerant-decode.md) | Feedback-Nachrichten belegt. Offen: AVPF-Feedback-Timing-Regeln (§3.5 Min-Interval) — Bursts möglich; rtcp-fb-PT auf `*` statt Offer-PT-Spiegelung (dokumentierte DECISION). |
| **RFC 3611** — RTCP XR | Extended-Report-Framing | **Teilweise** | [ADR-036](../../adr/ADR-036-rtcp-wire-codec-tolerant-decode.md); `RtcpWireCodec` | XR-**Decode** vorhanden. Offen: XR-**Send** fehlt (MAY). |
| **TWCC** — Transport-Wide Congestion Control (`transport-cc`, draft) | Seq-Stamping, Arrival-Recording, Feedback-Plane, AIMD-Bitrate-API | **Teilweise** | [ADR-038](../../adr/ADR-038-transport-cc-feedback-plane.md), [ADR-039](../../adr/ADR-039-transport-cc-estimator-bitrate-api.md); `VideoRtpStream`, `BundledOutboundTrack` | Feedback-Plane + Estimator + empfohlene-Bitrate-API belegt. Anmerkung: IETF-Draft (kein finaler RFC); RFC 8888 (RTCP-CC) nicht umgesetzt. |

---

## 4. Media-Security (SRTP / DTLS-SRTP)

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 3711** — SRTP/SRTCP | AES-CM-128/256, HMAC-SHA1-80/32, ROC §3.3.1, Replay-Window 64-bit, KDF §4.3, Key-Zeroing | **Voll** | [ADR-026](../../adr/ADR-026-srtp-media-path-fail-closed-hardening.md), [ADR-031](../../adr/ADR-031-srtcp-crypto-core-and-rtcp-path-wiring.md); `SrtpContext`, `SrtpKeyDerivation` | Krypto-Kern wire-korrekt (signed-delta-ROC, Verify-then-Decrypt). AES-256-Suite im Kern vorhanden. Kein MKI. |
| **RFC 4568** — SDES (Key-Management über SDP) | Offer/Answer mit Own-Key-Answers, Fail-Closed | **Voll** | [ADR-025](../../adr/ADR-025-sdes-offer-answer-negotiation.md) | Keyless → Reject (fail-closed) belegt. |
| **RFC 5763 / 5764** — DTLS-SRTP | Keying-Foundation, use_srtp-Ext, EXTRACTOR-Key-Export, Mutual-Cert | **Teilweise** | [ADR-028](../../adr/ADR-028-dtls-srtp-foundation.md), [ADR-029](../../adr/ADR-029-dtls-srtp-signaling-and-keying-precedence.md), [ADR-030](../../adr/ADR-030-dtls-srtp-media-wiring.md); `DtlsSrtpProfiles`, `DtlsMediaAttachment` | Handshake + Fail-Closed-Kontext belegt. **Angebotene Profile: nur AES-CM-128 (SHA1-80/32)** — kein AES-GCM/AEAD (RFC 7714) und kein AES-256-DTLS-Profil → Interop-Risiko gegen GCM-only-Browser. DTLS-Fingerprint nur SHA-256 (RFC 8122); nur DTLS 1.2 (BouncyCastle-Grenze). |

---

## 5. ICE / STUN / TURN

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 8445** — ICE | Rollen + Konflikt-487 + Tie-Break, Regular Nomination (USE-CANDIDATE), prflx via Triggered-Check, Pair-Priority §6.1.2.3 | **Teilweise** | [ADR-040](../../adr/ADR-040-send-side-ice-state-machine.md), [ADR-042](../../adr/ADR-042-ice-verification-and-shared-socket-gathering.md), [ADR-047](../../adr/ADR-047-video-ice-media-layer-and-candidates.md) | Send-Side-State-Machine belegt. Offen (MUST): volle Pair-State-Machine (Frozen/Waiting/In-Progress + Foundation-Freezing) — nur binäres Done-Flag; **ICE-Restart fehlt** (§9); STUN-Retransmission fix 200 ms statt RTO-Backoff. |
| **RFC 7675** — ICE Consent Freshness | Periodischer Consent-Check | **Teilweise** | [ADR-041](../../adr/ADR-041-consent-freshness-and-ice-restart-primitives.md); `IceMediaConsentSession` | Consent-Freshness als Primitive belegt; Restart-Primitive gebaut-aber-teils-unverdrahtet (siehe ADR-041). |
| **RFC 5389 / 8489** — STUN | Binding, Magic Cookie, XOR-MAPPED-ADDRESS, MI (HMAC-SHA1), FINGERPRINT (Encode+Verify), 420, RFC 7635 ACCESS-TOKEN | **Teilweise** | Gap-Register §STUN; `StunMessageCodec`, `StunKeyDerivation` | Kern belegt. Offen: MESSAGE-INTEGRITY-SHA256 (nur HMAC-SHA1), USERHASH, SOFTWARE-Emit, 300-Server-Seite. RFC-5769-Testvektoren: im internen Register als „vorhanden" behauptet, **Datei existiert nicht** (Overclaim, hier korrigiert). |
| **RFC 8656** — TURN (+ 6156 / 7635 / 6062 / 8016) | Allocate/Refresh/CreatePermission/ChannelBind, Send/Data-Indication, ChannelData, Quotas, EVEN-PORT/RESERVATION-TOKEN, DONT-FRAGMENT, IPv6-Family (6156), TCP-Relay (6062), Mobility (8016) | **Teilweise** | [ADR-054](../../adr/ADR-054-turn-relay-as-ice-candidate.md), [ADR-055](../../adr/ADR-055-turn-control-stack-allocation-permission-keepalive.md), [ADR-056](../../adr/ADR-056-post-nomination-whole-socket-relay-transition.md) | Server-Seite + Client-Allocation/Relay-Kandidat + Allocation-Refresh-Loop belegt. Offen (MUST, Client): **Permission-Refresh** (§9, dokumentierter Defer in `TurnRelayCandidateSendPath`) und **Channel-Rebind-Refresh** (§12) — Relay-Pfad bricht nach ~5/10 min still ab. Server: kein 420 auf TURN-Dispatch; ADDITIONAL-ADDRESS-FAMILY nur Stub. **TCP/TLS-Relay-Datenpfad und Voll-E2E gegen echten TURN-Server offen.** |

---

## 6. WebRTC-Interop (Browser-Pfad)

*Der WebRTC-Pfad ist im Aufbau. **Der Kern ist erstmals gegen einen echten Chrome bewiesen** —
SDK-Offerer ↔ Browser-Answerer, volle Kette Signaling→ICE→DTLS→SRTP, **bidir. Opus-Audio** (lokal-first;
[`../../audit/INTEROP_SOAK_AUDIT.md`](../../audit/INTEROP_SOAK_AUDIT.md), Coverage-Notiz Paket 1). Dabei
wurde der mDNS-`.local`-Blocker SDK-seitig behoben (RFC 8828). **Grenzen:** nur Audio, nur SDK-Offerer,
host-only. Video-Browser-Interop, die Browser-Offerer-Richtung und die unten gelisteten Wire-Gaps
(Opus-PT-Verhandlung, JSEP-State-Machine, RTCP/DTMF auf BUNDLE) bleiben offen → **kein
production-ready-Claim gegen Browser** bis zur vollständigen Interop-Verifikation
([ADR-009](../../adr/ADR-009-webrtc-browser-peer-roadmap.md)).*

| RFC | Bereich | Abdeckung | Beleg (ADR/Code) | Anmerkung |
|-----|---------|-----------|------------------|-----------|
| **RFC 8843** — BUNDLE | BUNDLE-Group + MID, geteilter Transport | **Teilweise** | [ADR-010](../../adr/ADR-010-bundle-transport-slice-plan.md), [ADR-011](../../adr/ADR-011-rtp-multitrack-transport.md) | Multi-Track-Transport belegt. Offen: `bundle-only`; **RTCP-Monitor auf BUNDLE-Pfad ist Stub** (kein SR/RR/RTT/BYE); **RFC 4733 DTMF wirft `NotSupportedException`** auf BUNDLE. |
| **RFC 8829** — JSEP | Signaling-State-Machine | **Nicht** | Gap-Register P0 #2 | Keine `RTCSignalingState` (stable/have-local-offer/…). Re-Offer/Rollback/Glare/Perfect-Negotiation strukturell nicht möglich — Wurzel-Schuld für ICE-Restart via JSEP. |
| **RFC 7874 / Opus** — WebRTC-Audio-Codec | Opus-PT-Verhandlung (PT 111) | **Nicht** | Gap-Register P0 #1; `SdpOfferAnswerNegotiator` | Opus als managed Codec vorhanden ([ADR-049](../../adr/ADR-049-opus-codec-integration-concentus.md)), aber **kein Opus-PT-Mapping in der Negotiation** → Browser-Audio-Call (Opus primär) scheitert. Härtester Browser-Interop-Blocker. |
| **RFC 8827/8828/8834** — WebRTC-Security/IP/RTP-Usage | DTLS-SRTP-Pflicht (fail-closed), mDNS-Inbound-Ignore, IP-Handling | **Teilweise** | [ADR-012](../../adr/ADR-012-webrtc-public-facade.md), [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md) | DTLS-SRTP-Pflicht + Mutual-Cert belegt. Offen: RFC-8828-IP-Handling-Modes (Host-IP direkt exponiert, kein mDNS-Emit). |
| **RFC 8853** — Simulcast | Send-side / recv-side (RID-Demux) | **Teilweise** | [ADR-060](../../adr/ADR-060-webrtc-facade-completion-and-server-hosting.md) | Send-side belegt. Recv-side RID-Demux bewusst offen. |
| **RFC 8831/8832** — SCTP DataChannels | — | **Nicht** | Gap-Register P2 | Komplett fehlend (MAY). |

---

## 7. Zusammenfassung für die Due Diligence

**Solide und wire-korrekt belegt:** SIP-Transaktions-Layer (alle Timer + RFC 6026), SIP-Wire-Codec
(§7/§20), der SRTP/DTLS-Krypto-Kern (ROC, Replay, KDF, Key-Export, Fail-Closed-Keying), SDES,
RTP-Header-Extensions (8285), RTX (4588) sowie die STUN/TURN-**Server**-MUST-Pfade und die
TLS-/Zertifikats-Sicherheit (RFC 5922 + BCP 195).

**Materielle offene Punkte (nach Cluster):**
1. **WebRTC/Browser-Interop** — Opus-PT-Verhandlung (7874), JSEP-State-Machine (8829),
   RTCP + DTMF auf dem BUNDLE-Pfad, AES-GCM in DTLS-SRTP (7714). Interop-schärfster Block.
2. **TURN-Client-Refresh** — Permission-/Channel-Rebind-Refresh (8656 §9/§12).
3. **ICE** — Pair-State-Machine + Foundation-Freezing, ICE-Restart (8445 §9).
4. **RTP/RTCP-Vollständigkeit** — SSRC-Kollisions-Antwort, RTCP-Intervall §6.2, CNAME-Uniqueness.
5. **SIP-Feinheiten** — voller Dialog-ID-Tag-Abgleich (§12.2), Strict-Router-Rewrite (§16.12).

**Bewusst out-of-scope:** SIP-Proxy-Rolle (§16), S/MIME, ein großer Block an Extension-RFCs
(MESSAGE, PUBLISH, Presence/MWI-Event-Packages, STIR/PASSporT u. a. — siehe SIP-Open-Topics-Register).

**Interop-Vorbehalt:** Alle „Voll/Teilweise"-Angaben sind primär Code-Belege. Für den
**SIP-/Audio-Kern** sind sie zusätzlich gegen **zwei echte SIP-Stacks** abgesichert — **Asterisk**
(im PR-CI-Gate) **und FreeSWITCH** (lokal-first, gleiche `IPbxFixture`-Szenario-Matrix; identischer
Testcode auf zwei Herstellern = Konformitätssignal) über die L0–L4-Interop-/Soak-Suite
([ADR-058](../../adr/ADR-058-layered-test-interop-soak-model.md)). Für den **WebRTC-/TURN-Pfad** gibt
es einen **ersten** realen Interop-Nachweis (TURN-Relay gegen echten in-Process-`TurnServer`; WebRTC-Kern
gegen echten Chrome, Connect + bidir. Audio, lokal-first). Offen bleiben: weitere Stacks (3CX/Fritzbox),
FreeSWITCH-CI-Gating sowie der **umfassende WebRTC-/TURN-/Browser-Interop-Nachweis** (Video-Browser-Interop,
Browser-Offerer-Richtung, externer coturn, CI-Gating des Browser-Tests).
