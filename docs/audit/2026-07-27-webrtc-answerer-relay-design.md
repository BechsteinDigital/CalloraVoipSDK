# WebRTC Answerer-Relay — Design (GA-Reifung)

**Ziel:** Den ICE-Answerer (controlled agent) seinen eigenen TURN-Relay-Kandidaten voll nutzen lassen — Connectivity-Check-**Responses** und **Consent** über den Relay senden, nicht nur direkt. Schließt den letzten großen WebRTC-GA-Code-Rest (NAT-Traversal, wenn der Answerer hinter symmetrischem NAT sitzt).

**Status:** ✅ UMGESETZT (Branch `feat/webrtc-answerer-relay`, Slices K1–K5, 2 Reviews APPROVED-WITH-FOLLOWUPS, in main). Der Answerer-Relay-Gap (GA-Blocker #2) ist geschlossen — der controlled Agent nutzt seinen eigenen Relay-Kandidaten über empfangspfad-getaggtes `replyVia`-Routing + proaktive TURN-Permission. Dieses Dokument ist das ursprüngliche Design.

<sub>(historisch) Design (nach Referenz-Recherche + Code-Verifikation). Sicherheitskritischer ICE-Kern — pro Slice fresh-context + Review.</sub>

---

## 1. Problem (code-verifiziert)

Der Answerer bewirbt seinen Relay-Kandidaten in der SDP, kann ihn aber nicht bedienen:

- **Response-Pfad direkt:** Ein inbound-Check erreicht den Answerer über dessen TURN-Server (Data-Indication). `BundledMediaTransport.DeliverInbound` entpackt sie (`BundledMediaTransport.cs:400`) und gibt den inneren Check mit `peer` = Offerer-Adresse weiter → `IceMediaAttachment.OnStunPacketReceived` (`IceMediaAttachment.cs:308`) → `IceInboundStunHandler` antwortet via **`_sendRaw` = direkt** (`IceInboundStunHandler.cs:129`). Hinter symmetrischem NAT erreicht die direkte Response den Offerer nie.
- **Consent direkt:** Der controlled-Nominierungspfad ist hartcodiert direkt: `Nominate(remote) => NominateInternal(remote, sendVia: null)` (`IceMediaAttachment.cs:255`), Kommentar: „a controlled agent … always uses the direct send path".
- **Kein Relay-Sendepfad:** `AddRelayLocalCandidate` ist No-Op ohne Driver (`IceMediaAttachment.cs:216`); der Driver existiert nur für `IceControlling` (`:104`). Der `relaySend` wird nur über `OnDriverNominated` (controlling) verdrahtet.

## 2. Referenz-Grundlage (libwebrtc · pjnath · libnice · Pion · SIPSorcery · aioice)

Alle sechs Stacks lösen das **identisch und role-agnostisch**: der Relay-Kandidat ist ein vollwertiger lokaler Kandidat mit eigenem Sendepfad (TURN-Socket/Wrapper). Checks, Responses **und** Consent laufen über den Sendepfad des lokalen Kandidaten des Paars; die Rolle steuert **nur** den Inhalt ausgehender Checks (USE-CANDIDATE / Tie-Breaker), **nie** die Wahl des Response-Transports. Ein „Answerer-Relay-Gap" existiert in keinem Referenz-Stack — er ist durch die Kandidaten-Abstraktion strukturell ausgeschlossen.

Belege: libwebrtc `Connection::SendResponseMessage` (kein Role-Check) über `TurnPort::SendTo`; **pjnath** kopiert die Response-`transport_id` verbatim aus dem Request-Token (`on_stun_rx_request`) → symmetrisch zurück über den Empfangspfad; libnice `priv_reply_to_conn_check` (kein Role-Param) über `nice_udp_turn_socket`; Pion controlling- **und** controlled-Selektor → dasselbe `sendBindingSuccess(msg, local, remote)`; **SIPSorcery** (direkteste C#-Referenz) `GotStunBindingRequest` wählt `SendRelay` per `wasRelayed`/`LocalCandidate.type==relay`, `IsController` nur für Nominierung; aioice `request_received` reicht das empfangende `TurnTransport` durch.

**Kernprinzip, das wir übernehmen:** *Die Response/Consent geht über den Pfad, auf dem der Request ankam* — deterministisch (pjnath-Stil: an den Request gebunden), nicht per globaler „last-received"-Heuristik, und ohne controlling/controlled-Sonderfall.

## 3. Architektur-Entscheidung: empfangspfad-getaggtes, role-agnostisches Routing

Der Transport **taggt** jeden inbound-STUN-Check mit seinem Reply-Pfad (`replyVia`: der Relay-Send-Delegate, wenn der Check aus einer Data-Indication entpackt wurde; sonst `null` = direkt). Dieser Tag wird durch die inbound-STUN-Kette gereicht, und der inbound-Handler beantwortet über `replyVia ?? sendRaw`. Führt der Check zur controlled-Nominierung, wird der Consent mit `sendVia = replyVia` nominiert (Consent folgt demselben Pfad).

Das ist **role-agnostisch**: derselbe Pfad-Wahl-Mechanismus gilt für controlling (heute schon per `OnDriverNominated`-`sendVia`) und controlled (neu). Es **vereinfacht** den Kern, statt einen Answerer-Sonderpfad zu bauen.

## 4. Komponenten (mit Anknüpfpunkten)

**K1 — Empfangspfad-Tagging + `replyVia` durch die inbound-STUN-Kette.**
`BundledMediaTransport.DeliverInbound`: bei erfolgreichem `_indicationRelay.TryUnwrap` (`:400`) den inneren STUN-Check mit einem `replyVia`-Delegate weitergeben, der über den Relay framed (= der `relaySend` des Answerers). Der Tag wird durch `BundledInboundPipeline.StunPacketReceived` (Event-Signatur um einen optionalen `replyVia` erweitern) → `IceMediaAttachment.OnStunPacketReceived` → `IceInboundStunHandler.OnStunPacketReceived` gereicht. Direkter Empfang: `replyVia = null` (byte-identisch zu heute).

**K2 — Role-agnostisches Response-Routing.**
`IceInboundStunHandler.SendResponseAsync` (`:125`) sendet über `replyVia ?? _sendRaw`. Die STUN-Decode/Auth/MESSAGE-INTEGRITY-Validierung (`IceInboundCheckProcessor`) bleibt **unverändert** — nur der Transport der fertigen Response ändert sich.

**K3 — Consent über den Relay bei Relay-empfangener Nominierung.**
`IceMediaAttachment.Nominate` (controlled, `:255`) wird `NominateInternal(remote, sendVia: replyVia)` — der `replyVia` des USE-CANDIDATE-Checks, der die Nominierung auslöste. `IceMediaConsentSession.Nominate(remote, sendVia)` (`:117`) trägt das schon; `SendCheckVia(nominated.Send ?? _sendRaw, …)` (`:143`) sendet dann Consent relay-geframed. Der triggered-check-Pfad (`SendTriggeredCheckAsync`, `:162`) muss den Relay-Pfad ebenso respektieren (measure-first: derselbe replyVia).

**K4 — Proaktive Permission für Remote-Kandidaten-IPs (measure-first ✓ geklärt).**
Damit inbound-Checks über den Relay überhaupt ankommen, muss der Answerer eine TURN-Permission (§9) für die IP jedes Offerer-Remote-Kandidaten installieren, **bevor** der Offerer prüft. Anknüpfpunkt (verifiziert): Remote-Kandidaten landen bei `IceMediaAttachment.AddRemoteCandidate` (`:196`) → `_nominationDriver?.AddCandidate` = **No-Op beim Answerer** (kein Driver) — genau hier kennt der Answerer die Offerer-IP. Die Permission-Mechanik existiert: `TurnRelayCandidateSendPath.EnsurePermissionAsync` (`:151`) installiert Permission pro Peer-IP mit Dedup (Lazy), proaktiv aufrufbar. Die schon gebaute `TurnPermissionRefreshLoop` hält sie am Leben. **Design-Verfeinerung:** `AddRelayLocalCandidate` (`:212`) darf beim Answerer nicht komplett No-Op sein — es speichert den `_relaySend` (auch ohne Driver), sodass `AddRemoteCandidate` proaktiv Permission für die Remote-IP installiert. Der controlling-Driver-Pfad bleibt unberührt (K6).

**K5 — Verdrahtung im Answerer-Pfad.**
`BundledMediaSession.AdoptRelay` (`:572`) gibt dem Transport den `relaySend` (für K1's `replyVia`) — heute via `SetIndicationRelay` schon halb da. `WebRtcRelayBinding.CreateFactory` liefert den `RelayIceBinding` (Indication + relaySend + KeepAlive); der Answerer-Zweig (`WebRtcPeerConnection.GatherRelayAsync` → `AdoptRelay`) existiert. Neu: der `replyVia`/Consent-Pfad wird durchgereicht.

**K6 — controlling-Driver-Pfad subsumiert.**
`OnDriverNominated` (`:246`) setzt heute `sendVia = _relaySend` bei Relay-Nominierung — das bleibt (controlling). Das neue empfangspfad-getaggte Routing ergänzt den controlled-Pfad, ohne den controlling-Pfad zu ändern (verhaltensbewahrend für den Offerer).

## 5. Sicherheit

Der inbound-Check wird weiterhin voll validiert (MESSAGE-INTEGRITY / short-term credential, `IceInboundCheckProcessor`) **bevor** eine Response erzeugt wird — die Pfad-Wahl (relay/direct) ändert daran nichts, sie betrifft nur den Transport der bereits authentifizierten Response. Kein Credential-Bypass. Der Relay-Sendepfad nutzt die schon authentifizierten Allocation-Credentials.

## 6. Verifikation

- **Unit:** `replyVia` wird durch die Kette gereicht; inbound-Handler sendet Response über `replyVia` wenn gesetzt, sonst `sendRaw` (byte-identisch ohne Tag). Consent nominiert mit `sendVia = replyVia` bei Relay-empfangener Nominierung.
- **E2E (Kern-Nachweis):** Answerer hinter „totem" Direct-Pfad + Fake-TURN-Server: der Offerer nominiert das Relay-Pair, der Answerer beantwortet den Check + führt Consent über den Relay → Verbindung kommt zustande. Analog zu den bestehenden `BundledIceControlRelayTests`, aber controlled-Seite.
- **Regressions-Gate:** voller ICE/Relay-Suite grün; alle TFMs warnings-as-errors + ArchitectureTests.

## 7. Sub-Slices (TDD, je Commit)

1. **K1+K2 (Transport→inbound `replyVia`):** Empfangspfad-Tag durch die STUN-Kette + role-agnostisches Response-Routing. Verhaltensbewahrend ohne Relay (replyVia=null). Unit-Tests.
2. **K3 (Consent-Relay bei controlled-Nominierung):** `Nominate` trägt `replyVia` → Consent relay-geframed. Unit-Tests.
3. **K4 (proaktive Permission):** Remote-Kandidaten-IP → CreatePermission über den Relay. Measure-first: Anknüpfpunkt verifizieren. Unit/Integration.
4. **K5 (Answerer-Verdrahtung + E2E):** `AdoptRelay`/`GatherRelayAsync` reichen den Pfad durch; E2E Answerer-Relay gegen Fake-TURN.

## 8. Offene measure-first-Punkte (beim Bauen zu klären)

- **K4-Anknüpfpunkt:** genaue Stelle, wo der Answerer Remote-Kandidaten-IPs kennt und Permission installiert (proaktiv). Ohne inbound-Permission kommt kein Check an.
- **`replyVia`-Lebensdauer:** der Delegate wird fire-and-forget in der Response genutzt; er muss bis dahin gültig sein (dieselbe Lifetime wie der Relay-Sendepfad; Teardown-Ordnung wie beim bestehenden KeepAlive).
- **Simulcast/Mehrfach-Remote:** ein Answerer mit mehreren Offerer-Kandidaten braucht Permission je IP (dedup wie im Send-Path schon).
- **Interaktion mit dem Whole-Socket-Relay-Modus (§11/§12 ChannelData):** K1 gilt für den Direct-Mode-Indication-Pfad (Check-Phase); nach der Relay-Transition (ChannelData) läuft Media anders. Konsistenz prüfen.
