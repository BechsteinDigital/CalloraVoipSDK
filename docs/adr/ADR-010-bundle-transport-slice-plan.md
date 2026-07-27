# ADR-010: BUNDLE Media-Transport — Slice-Plan

Status: Accepted
Date: 2026-07-15

## Context

WebRTC-Browser verlangen BUNDLE (RFC 8843) zwingend: ein gemeinsames 5-Tupel / UDP-Socket /
DTLS-Assoziation / ICE-Agent für alle `m=`-Sektionen, mit Track-Routing über MID / Payload
Type / SSRC. BUNDLE ist damit der eigentliche MVP-Blocker für Browser-Calls (ADR-009 §2).
Der Founder war explizit: `Bundle = true` in der SDP-Generierung ohne den Transport-Umbau
wäre technisch falsch — der Transport muss zuerst.

## Verifizierter Ist-Zustand (2026-07-15, vier parallele Explore-Läufe)

Bereits vorhanden (Fundament):
- **Paket-Typ-Demux** STUN/DTLS/RTP/RTCP auf EINEM Socket — `RtpSession.ProcessDatagram`
  (RFC 7983: STUN byte 0–3 + magic cookie; DTLS 20–63; RTCP v2 PT 192–223; sonst RTP).
- **rtcp-mux** (RFC 5761) — ein Socket für RTP+RTCP.
- **DTLS-SRTP** reif, **ICE + consent** reif, **SDES** als Alternative.
- **SDP-BUNDLE-Modell** — `SdpSessionDescription.Group` (`a=group:BUNDLE`),
  `SdpMediaDescription.Mid` (`a=mid`) inkl. Parse/Serialize; RFC-8285-Header-Extension-Parsing
  (`OneByteRtpHeaderExtensions`, 0xBEDE).

Fehlt / getrennt:
- **Audio und Video laufen auf komplett GETRENNTEN Transports**: je eigener UDP-Socket/Port
  (`CallVideoParameters.LocalEndPoint`), eigene DTLS-Assoziation (RFC 5763 one-per-m-line),
  eigenes ICE-Consent (shared ufrag/pwd, aber separates 5-Tupel), eigene SRTP-Kontexte.
  Video = `VideoRtpStream` mit eigener `RtpSession`.
- **Kein Multi-Track-Routing**: jeder Socket geht exklusiv an EINEN Stream. Kein MID/PT/SSRC-
  Routing mehrerer Media-Streams auf einem Transport (nur RTX-Secondary-PT existiert).
- **MID-RTP-Header-Extension** (`urn:ietf:params:rtp-hdrext:sdes:mid`, RFC 8843 §5.2) wird
  NICHT dekodiert (Routing-Grundlage fehlt).
- **SDP-BUNDLE-Generierung deaktiviert**: `SdpMediaNegotiationOptions` hat kein `Bundle`-Feld;
  `SdpUtilities.ConvertOptions` setzt Bundle nie → `Group`/`Mid` bleiben null.

## Decision

BUNDLE wird in dieser Reihenfolge gebaut. Der Transport-Umbau kommt VOR der SDP-Aktivierung,
damit zu keinem Zeitpunkt BUNDLE offeriert wird, das der Transport nicht bedienen kann.

- **B1 — MID-RTP-Header-Extension** (klein, in sich geschlossen, erster Slice)
  Decode + Encode für `urn:ietf:params:rtp-hdrext:sdes:mid` (RFC 8843 §5.2 / RFC 8285) auf
  Basis des vorhandenen `OneByteRtpHeaderExtensions`. Reine Wire-Logik + Unit-Tests, keine
  Transport-Änderung. Die Routing-Grundlage.

- **B2 — Multi-Track-Demux auf einem Transport** (Kern-Umbau, groß)
  Eine Transport-Einheit (`BundledMediaTransport`) über einem Socket, die inbound RTP/RTCP
  nach MID (→ PT → SSRC als Fallback, RFC 8843 §9.2) dem richtigen Track (Audio/Video) routet.
  Baut auf dem vorhandenen Paket-Typ-Demux auf.

- **B3 — Geteiltes DTLS + ICE-Consent** für den gebündelten Transport
  EINE DTLS-Assoziation und EIN Consent-Monitor für alle Tracks (statt pro m-line). Die SRTP-
  Kontexte werden geteilt.

- **B4 — Video über den geteilten Transport**
  `VideoRtpStream` nutzt den gebündelten Transport statt eigener `RtpSession`/Socket/DTLS/ICE;
  die interne Video-Logik (Codec-Packetisierung, Keyframe-Feedback, Reorder-Buffer, transport-cc)
  bleibt. Audio-only-Legs unverändert.

- **B5 — SDP-BUNDLE-Generierung aktivieren** (zuletzt)
  `SdpMediaNegotiationOptions.Bundle` (public), `ConvertOptions` → `Bundle`, `CreateOffer`:
  `a=group:BUNDLE`, alle m-lines auf einen Port (bundle-only Port 0 für non-first, RFC 8843 §5),
  session-level ICE, rtcp-mux erzwungen. Answer spiegelt vorhandenes BUNDLE (existiert teils).

- **B6 — Browser-Interop-Validierung** (Chrome/Firefox) — eigener Meilenstein.

Track-Identity (`a=msid`, Stream/Track-IDs) läuft parallel als eigener Slice (ADR-009 §3), baut
auf B1/B4 auf. Simulcast (`a=rid`) folgt später.

## Consequences

Positive: Der Paket-Typ-Demux + rtcp-mux + DTLS/ICE/SRTP sind schon da — der Umbau ist eine
Multi-Track-Schicht darüber, kein Neubau von Grund auf. Audio-only-Legs bleiben unberührt.

Tradeoffs: B2–B4 berühren den zentralen Media-Transport (`RtpSession`, `RtpCallMediaSession`,
`VideoRtpStream`, `DtlsMediaAttachment`, `IceMediaAttachment`) — timing- und krypto-sensitiv.
Jeder Slice braucht E2e-Tests vor dem nächsten. Bis B4 fertig ist, wird BUNDLE nicht offeriert
(B5), also bleibt der Nicht-BUNDLE-Pfad (getrennte m-lines) bis dahin der einzige Weg.

## Guardrails

- Kein `a=group:BUNDLE` im Offer, bevor der Transport es bedient (B5 nach B2–B4).
- Audio-only-Pfad zu jedem Zeitpunkt grün und unverändert.
- Kein WebRTC/BUNDLE-Code im SIP-Signaling-Kern (bleibt im Media-Transport / Infrastructure/Rtp).
