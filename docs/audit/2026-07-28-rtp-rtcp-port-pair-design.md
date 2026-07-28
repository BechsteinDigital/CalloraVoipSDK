# RTP/RTCP-Portpaar-Reservierung — Design (2026-07-28)

**Branch:** `fix/rtp-rtcp-port-pair-reservation` · **Ziel:** das `Failed to bind RTCP socket … Address already in use`-Race unter Last beseitigen.

## Problem (measure-first, verifiziert)

Ein SIP-Call reserviert nur den RTP-Port (`SipCoreCallChannel` bindet `_localMediaSocket` auf Port 0 → N). Der RTCP-Port wird als **N+1** ins SDP geschrieben (`SdpUtilities.ResolveRtcpPort`), aber **nie gebunden/gehalten**. Der `CallRtcpQualityMonitor` bindet N+1 erst **spät** in `StartAsync` (`new UdpClient(_localRtcpEndPoint)`); ist er inzwischen belegt → `SocketException` → `catch` → Qualitätsmessung für diesen Call aus (RTP-Audio läuft weiter). Je mehr parallele Calls, desto wahrscheinlicher belegt ein späterer RTP-Zufallsport das ungeschützte N+1 eines früheren Calls.

Der RTP-Port hat dieselbe Klasse Fehler in klein: Reserve → **Release** (`ReleasePortReservationSockets`) → **Rebind** durch `RtpSession` = Mikrosekunden-TOCTOU. Beobachtet bricht aber RTCP, weil es das ganze Setup über ungeschützt ist.

## Fix

Konsekutives Portpaar **atomar reservieren, halten und übergeben** — kein Release+Rebind. Ein Portwechsel nach SDP-Veröffentlichung wäre zu spät (das SDP hat N+1 bereits angekündigt).

1. **`MediaPortReservation`** (Infra, `IDisposable`): bindet RTP auf zufälliges N, dann N+1; N+1 belegt → dispose + Retry mit neuem N (bounded). Hält beide `UdpClient` bis zur Übergabe; `Take…Socket()` überträgt Ownership (Reservation schließt nur nicht-übernommene Sockets). Audio + Video je eine Instanz.
2. **Pre-bound-Socket-Seam** in `RtpSession` + `CallRtcpQualityMonitor` (+ `VideoRtpStream`): optionaler `UdpClient`; gesetzt → Ownership übernehmen statt neu binden; `null` → heutiges Verhalten (verhaltensbewahrend, ICE-Pfad unberührt — dort besitzt der ICE-Agent den Socket).
3. **Handoff über einen Infra-Sidecar**, NICHT das Domain-`record` `CallMediaParameters` (kein Live-Socket im Value Object). Die Reservierung reist von `SipCoreCallChannel` zu `RtpCallMediaSessionFactory` + `CallMediaOrchestrator`.

## Slices

1. `MediaPortReservation` + Unit-Test (inkl. Kollisions-Retry).
2. Socket-Seams in `RtpSession`/`CallRtcpQualityMonitor`/`VideoRtpStream` (verhaltensbewahrend bei `null`).
3. Verdrahtung: `SipCoreCallChannel` → Sidecar → Factory/Orchestrator → Übergabe; Release+Rebind entfällt (Nicht-ICE).
4. Deterministischer Kollisionstest (Fremd-Socket auf N+1 → RTCP bleibt mit Fix aktiv), Review, PR.

## Nicht in Scope (Follow-up)

Media-Hotpath-Profiling (`MediaReceiver` erzeugt pro Frame EventArgs + Invocation-List-Kopie); Server-GC-Rampentest.
