# Lokale ICE-Restart-Initiation — Design (2026-07-28)

**Branch:** `feat/webrtc-ice-restart` · **Issue:** #62 Punkt 2 · **Ziel:** `ICall.RestartIceAsync()` — der lokale Endpunkt initiiert einen ICE-Restart (RFC 8445 §9 / RFC 8829 JSEP) auf einem laufenden Call, ohne den Media-Socket oder die DTLS/SRTP-Sicherheit neu aufzubauen.

## Referenz-Recherche (Kurzfassung)

- **RFC 8445 §9:** Ein ICE-Restart MUSS **beide** Credentials wechseln (ice-ufrag UND ice-pwd). Rolle/Tiebreaker werden **nicht** neu bestimmt (außer bei Full→lite-Wechsel oder Role-Conflict). Media läuft auf dem „previously validated pair" weiter, bis die neue Nominierung steht.
- **RFC 8829 (JSEP) §5.2.3.1:** `createOffer({iceRestart:true})` erzeugt neue Credentials; der Signaling-Austausch (Re-Offer/Re-Answer) ist zwingend.
- **Referenz-Impls:** pjnath (`stop_ice`+`init_ice`, **Sockets wiederverwendet**) und libwebrtc (`RestartIce()` → deferred bis `createOffer`, Allocator/Session wiederverwendet) belegen **Variante (a): bestehenden Transport/Socket behalten, nur neue Credentials + neue Check-List + neues Gathering.** SIPSorcery hat `restartIce()`, aber die Credentials sind `readonly` = RFC-Verstoß — genau der Fehler, den wir vermeiden.

## Measure-first-Befund: die Re-Negotiation-Maschinerie existiert bereits

- `CallMediaOrchestrator.OnMediaParametersNegotiated` stempelt **jede** Re-Negotiation mit einer monotonen Generation (#10), ruft `SelectCandidatePairAsync` (echte STUN-Checks) und installiert eine neue Media-Session, verwirft die alte. Das **ist** der Reuse-Socket-Swap.
- `CallIceAgent.BuildLocalDescriptionAsync` erzeugt bei **jedem** Aufruf frische ufrag+pwd (RFC 8445 §9 erfüllt) und gathert über den **geteilten Media-Socket** (`_localMediaSocket.Client`) → Socket-Reuse ist der Normalfall.
- `SipCoreCallChannel.EnsureLocalIceDescriptionAsync` **cached** `_localIceDescription` (für Hold/Unhold korrekt: gleiche Creds). Der Restart durchbricht genau diesen Cache.
- `SipCallSession.HoldAsync`/`UnholdAsync` rufen dasselbe Primitive `SendInviteTransactionAsync(body, allowRingingTransition:false, successState:…, ct)`; nur `successState` + Vorbedingung unterscheiden sich.

**Fazit:** ICE-Restart ist ein *Initiations*-Feature, keine Neuimplementierung des ICE-Kerns.

## Scope

**Nur SIP-Pfad.** `SipCoreCallChannel` ist der einzige `ICallChannel`; `ICall` ist konstruktiv SIP-backed. Der WebRtc-Peer-Pfad (`WebRtcPeerConnection`) ist eine separate API — dessen Re-Offer-/Renegotiation-Guard (`SetRemoteDescriptionAsync:304`) bleibt unangetastet und weiterhin dokumentiert Post-GA. `ICall.RestartIceAsync` fasst ihn nicht an.

### Entscheidungen

- **Nicht-ICE-Call:** `RestartIceAsync` wirft `InvalidOperationException` („ICE nicht ausgehandelt") statt still zu no-oppen — Fehlkonfiguration soll sichtbar sein.
- **Vorbedingung:** `CallState.Connected` (wie `HoldAsync`).
- **Rolle/Tiebreaker:** `_iceControlling` wird über den Restart **erhalten** (RFC 8445 §9), nicht neu gewürfelt.
- **Richtung:** sendrecv (der Restart ändert die Media-Richtung nicht).

## Mechanik (`SipCoreCallChannel.RestartIceAsync`)

1. `_localIceDescription = null` → Credential-Cache invalidieren.
2. `EnsureLocalIceDescriptionAsync(localEndPoint, ct)` erneut → frische ufrag+pwd, Gathering auf **demselben** Socket (`_localMediaSocket.Client`).
3. `BuildDefaultSdp(localEndPoint, hold:false, BuildReofferSdpOptions())` → Re-Offer mit neuem ICE-Block, `_iceControlling` unverändert.
4. `session.ReinviteAsync(reofferSdp, ct)` → in-dialog Re-INVITE, `successState:Established`.
5. Answer kommt als `RemoteSdp` auf dem Established-Übergang zurück → `TryRepublishOnRekey` → `OnMediaParametersNegotiated` (neue Generation) → `SelectCandidatePairAsync` auf altem Socket → neue Session installiert, alte verworfen. Media-Kontinuität aus dem Generations-Swap.

## Slices

1. **`ISipCallSession.ReinviteAsync(sdp, ct)`** + `SipCallSession`-Impl (richtungserhaltender Re-INVITE, Vorbedingung `Established`, `successState:Established`). Test: sendet Re-INVITE mit gegebenem Body, bleibt Established.
2. **`ICallChannel.RestartIceAsync()`** + `SipCoreCallChannel.RestartIceAsync()` (Cache-Invalidierung → Re-Gather gleicher Socket → Re-Offer neue Creds → `ReinviteAsync`; `_iceControlling` erhalten; Nicht-ICE → wirft). Test: neue ufrag+pwd ≠ alte, Rolle erhalten, Media-Port unverändert.
3. **`ICall.RestartIceAsync(ct)`** + `Call.RestartIceAsync` (Guard `Connected`, Delegation). Test: Guard + Delegation.
4. **E2E** (falls Harness vorhanden): Restart auf laufendem ICE-Call → neue Nominierung, Media-Kontinuität über den Swap.

## ★ Umgesetzt (2026-07-28)

Slices 1–3 umgesetzt in `471a4afc`; Review-Fix in `3766ee66`. Ergebnis:

- **Slice 1** `ISipCallSession.ReinviteAsync` + `SipCallSession` — Hold/Unhold/Reinvite teilen jetzt einen gemeinsamen `SendReInviteAsync`-Helfer (DRY, verhaltensbewahrend; hielt `SipCallSession.cs` unter der 1000-Zeilen-Regel).
- **Slice 2** `SipCoreCallChannel.RestartIceAsync` — Cache-Invalidierung → Re-Gather gleicher Socket → Re-Offer neue Creds → `ReinviteAsync`; `_iceControlling` erhalten.
- **Slice 3** `ICall`/`Call.RestartIceAsync` — Guard `Connected`, Delegation.
- **Review-Fix:** Guard prüft zusätzlich `RemoteOfferHasIce(session.RemoteSdp)` (RFC 8445 §5.4) — ein ausgehender Call mit ICE-fähigem SDK aber Plain-RTP-Peer wirft jetzt, statt ICE-Credentials an einen Nicht-ICE-Peer zu senden.

**Test-Deckung:** `SipCoreCallChannelIceRestartTests` (neue Creds ≠ alte, gleicher Media-Port = Socket-Reuse, sendrecv, Nicht-ICE-Agent wirft, Peer-declined-ICE wirft), `CallIceRestartTests` (Guard + Delegation). Der Media-Kontinuitäts-**Round-Trip** (Re-Offer-Answer → Republish → neue ICE-Selektion auf altem Socket) läuft über den bestehenden #10-Generations-Swap und ist bereits durch `SrtpReofferContinuityTests` + `SipCoreCallChannelRekeyTests` gedeckt.

**Slice 4 (Live-Media-Flow-E2E) bewusst nicht gebaut:** ein bespoke End-to-End-Restart mit echtem Media-Fluss (zwei Live-ICE-Agenten, STUN über Loopback) wäre redundant zur obigen Mechanik-Deckung und gehört inhaltlich zu **#62 Punkt 3** (Live-Interop/NAT-Matrix-Validierung) — dort gegen echte Peers (Asterisk/Browser) verifizieren.

## Nicht in Scope (Follow-up)

- **Inbound-Restart** (Peer initiiert): `IceRestartDetector` in Produktion verdrahten + Doku-Fix „both" statt „and/or". Eigenes Paket — #62 Punkt 2 ist explizit *Initiation*.
- WebRtc-Peer-Renegotiation (`WebRtcPeerConnection` Re-Offer) — Post-GA.
