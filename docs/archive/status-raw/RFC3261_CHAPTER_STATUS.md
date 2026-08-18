# RFC 3261 Kapitelstatus (inkl. Unterkapitel)

Stand: 2026-04-09
Quelle: https://datatracker.ietf.org/doc/html/rfc3261

> **Hinweis:** Dieses Dokument enthaelt ausschliesslich den RFC-3261-Kapitelstatus.
> Die vollstaendige VoIP-SDK-RFC-Referenz (SIP-Erweiterungen, SDP, RTP, SRTP, ICE, STUN, TURN, Codecs, WebRTC)
> befindet sich in: [RFC_VOIP_SDK_COMPLIANCE.md](./RFC_VOIP_SDK_COMPLIANCE.md)

Hinweis: `Erledigt` ist nur dann `Ja`, wenn der Abschnitt als vollstaendig und belastbar umgesetzt bewertet ist. Aktuell wird streng konservativ bewertet.

## Compliance Target

Verbindliches Zielbild fuer dieses Dokument und alle Kapitelbewertungen:

- RFC 3261 Core vollstaendig und belastbar.
- Relevante Update-RFCs sind eingearbeitet (nicht nur Stand 2002).
- Bewusst gewaehlte Erweiterungen sind klar als "supported" dokumentiert.
- Nicht umgesetzte Themen sind klar als "unsupported/out of scope" markiert.
- Kapitel gelten nur dann als `Erledigt`, wenn Core + relevante Updates fuer den jeweiligen Scope nachweisbar abgedeckt sind.

## RFC-3261-Update-Matrix (Kurzreferenz)

Folgende RFCs aendern RFC 3261 normativ – Details im Gesamtdokument:

| RFC | Betroffene Kapitel | Impact |
|---|---|---|
| RFC 3262 | §8.2.6.1, §13.2.2.1, §13.3.1.1, §17.2.1 | Reliable Provisional Responses (PRACK/100rel) |
| RFC 3265→6665 | §8, §10 | Event Notification Framework (SUBSCRIBE/NOTIFY) |
| RFC 3581 | §18.2.1, §18.2.2, §20.42 | Symmetric Response Routing (rport) |
| RFC 4320 | §17.1.2, §17.2.2 | Non-INVITE Transaction Timer-Aenderungen |
| RFC 5626 | §8.1.2, §10.2, §18, §20.42 | Outbound (Connection-Oriented Transport) |
| RFC 6026 | §13.2.2.4, §13.3.1.4, §17.1.1, §17.2.1 | Korrektes 2xx-Handling fuer INVITE |
| RFC 6141 | §12.2, §13, §14 | re-INVITE und Target-Refresh neu spezifiziert |
| RFC 7616 | §22 | HTTP Digest SHA-256/SHA-512 (ersetzt RFC 2617) |
| RFC 8217 | §7.3, §8.1.1, §19, §20.x | name-addr/addr-spec Klarstellungen |
| RFC 8760 | §22 | Digest Auth Staerke-Anforderungen |

| Abschnitt | Titel | Erledigt | Stand | Hinweis |
|---|---|---|---|---|
| 1 | Introduction | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 2 | Overview of SIP Functionality | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 3 | Terminology | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 4 | Overview of Operation | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 5 | Structure of the Protocol | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 6 | Definitions | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 7 | SIP Messages | Ja | Erledigt | Abschnitt 7 ist konsistent abgeschlossen: 7.1 Requests, 7.2 Responses, 7.3 Header Fields, 7.4 Bodies und 7.5 Framing SIP Messages sind umgesetzt und per Compliance-Tests abgesichert. |
| 7.1 | Requests | Ja | Erledigt | Request-Line-Regeln aus RFC3261 7.1 sind umgesetzt (single SP, SIP/2.0, Method-Token, Request-URI-Validierung inkl. unescaped whitespace/control und kein `<...>`), inkl. Wire-Compliance-Tests. |
| 7.2 | Responses | Ja | Erledigt | Status-Line-Regeln aus RFC3261 7.2 sind umgesetzt (single SP fuer Trennstellen, SIP/2.0, 3-stelliger Status-Code in 1xx-6xx, Reason-Phrase ohne CR/LF Injection), inkl. Wire-Compliance-Tests. |
| 7.3 | Header Fields | Ja | Erledigt | Header-Folding, Duplicate-Row-Verarbeitung mit RFC-Ausnahmen (Auth-Header nicht comma-kombiniert), Compact-Form-Akzeptanz und Klassifikations-Ignorierregeln sind umgesetzt und getestet. |
| 7.3.1 | Header Field Format | Ja | Erledigt | Header-Format inkl. LWS um `:`, Folded-Lines (`SP/HT`), Reihenfolge-erhaltende Duplicate-Row-Verarbeitung sowie Non-Combine-Ausnahmen fuer Auth-Header sind im Wire-Parser umgesetzt. |
| 7.3.2 | Header Field Classification | Ja | Erledigt | Request-/Response-only Header werden beim Parsing RFC-konform fuer den unpassenden Nachrichtentyp ignoriert (Core-Header-Klassifikation aktiv). |
| 7.3.3 | Compact Form | Ja | Erledigt | Long/Compact Headerformen werden in gemischten Nachrichten akzeptiert und auf kanonische Namen normalisiert (inkl. `e`/`Content-Encoding`). |
| 7.4 | Bodies | Ja | Erledigt | Body-Verarbeitung ist fuer Requests/Responses gehaertet: typed body Regeln, leere Body-Sonderfaelle, und serialisierungsseitige RFC-Validierung sind umgesetzt und getestet. |
| 7.4.1 | Message Body Type | Ja | Erledigt | Nicht-leerer Body erfordert `Content-Type`; `Content-Encoding` bei leerem Body wird verworfen; Compact-/Long-Formen werden akzeptiert und normalisiert. |
| 7.4.2 | Message Body Length | Ja | Erledigt | Byte-genaue `Content-Length`-Verarbeitung inkl. konsistenter Mehrfachwerte, CL=0 fuer leeren Body im Serializer, Verbot von `Transfer-Encoding: chunked`, und harte Parse-Validierung ist implementiert. |
| 7.5 | Framing SIP Messages | Ja | Erledigt | Stream-Framing folgt RFC3261: fuehrende CRLF vor Start-Line werden ignoriert, Frames werden strikt ueber `Content-Length` begrenzt, und streambasierte Nachrichten ohne `Content-Length` werden verworfen; Compliance-Tests vorhanden. |
| 8 | General User Agent Behavior | Nein | Teilweise | UAC/UAS-Kernverhalten wurde gehaertet (Pflichtheader, Request-URI-Schema-Checks, To-Tag in non-100 Responses, UAC-Response-Normalisierung), aber Redirect-/Retry-/Corner-Case-Vollabdeckung fehlt. |
| 8.1 | UAC Behavior | Ja | Erledigt | UAC Abschnitt 8.1 ist abgeschlossen: Request-Generierung, Routing/Sende-Fallback und Response-Policy inkl. Redirect/4xx/Transportfehler sind implementiert und durch `SipUacSection8ComplianceTests` plus SIP-Regressionstests abgesichert. |
| 8.1.1 | Generating the Request | Ja | Erledigt | 8.1.1-Headerregeln inkl. Request-URI/To/From/Call-ID/CSeq/Max-Forwards/Via/Contact/Require-Proxy-Require sind umgesetzt; preloaded Route-Set (strict/loose) fuer initiale Requests ist verdrahtet. |
| 8.1.1.1 | Request-URI | Ja | Erledigt | Request-URI-Validierung fuer SIP/SIPS (inkl. host-only SIP URIs) ist umgesetzt; Nicht-SIP-Schemes werden verworfen. Initiales preloaded Route-Set mit strict/loose Routing wird fuer Request-URI/Route/Next-Hop angewendet und getestet. |
| 8.1.1.2 | To | Ja | Erledigt | Out-of-dialog Requests werden ohne To-Tag erzeugt (INVITE/REGISTER Tests vorhanden). |
| 8.1.1.3 | From | Ja | Erledigt | UAC erzeugt From mit lokalem Tag; in Tests fuer INVITE/REGISTER abgesichert. |
| 8.1.1.4 | Call-ID | Ja | Erledigt | Out-of-dialog Requests enthalten stabilen Call-ID und behalten ihn bei Retry-Pfaden (Auth/423). |
| 8.1.1.5 | CSeq | Ja | Erledigt | CSeq-Methodenabgleich und positive Sequenznummer werden bei Client-Transactions validiert; Headeraufbau nutzt methodenkonsistente CSeq-Werte. |
| 8.1.1.6 | Max-Forwards | Ja | Erledigt | UAC setzt Max-Forwards=70 und Client-Transaction-Validierung erzwingt gueltigen Max-Forwards-Header. |
| 8.1.1.7 | Via | Ja | Erledigt | UAC-Requests enthalten Via-Branch mit RFC3261 Magic-Cookie; Client-Transaction validiert das. |
| 8.1.1.8 | Contact | Ja | Erledigt | INVITE-Requests tragen genau einen Contact; bei SIPS-Request-Target/Top-Route wird SIPS-Contact erzwungen. |
| 8.1.1.9 | Supported and Require | Ja | Erledigt | Require/Proxy-Require koennen outbound gesetzt werden; UAC verarbeitet 420/Unsupported durch token-basiertes Retry-Filtering; INVITE-UAS validiert Require und liefert 420+Unsupported fuer unbekannte Optionen. |
| 8.1.1.10 | Additional Message Components | Ja | Erledigt | UAC erzeugt methodenspezifische Zusatzkomponenten (Content-Type, Session-Timer, Identity, Route, User-Agent, Trace-Korrelation) konsistent ueber Initial-INVITE-Generierung. |
| 8.1.2 | Sending the Request | Ja | Erledigt | UAC sendet ueber RFC3263-Kandidatenlisten (DNS/Transport), inkl. Ziel-Failover, Redirect-Zielverfolgung und erneuter Zustellung gemaess Retry-Policy. |
| 8.1.3 | Processing Responses | Ja | Erledigt | 8.1.3.1-8.1.3.5 sind implementiert: Transaction-Error-Abbildung, unrecognized-response Normalisierung, Via-Pruefung, 3xx Redirect-Verarbeitung und 4xx-spezifische Retry-Pfade (401/407/413/415/416/420). |
| 8.1.3.1 | Transaction Layer Errors | Ja | Erledigt | Transaction-Layer Fehler werden am UAC als synthetische finale SIP-Fehler abgebildet (408 fuer Timeout, 503 fuer Transportversagen), statt als stumme Abbrueche zu verschwinden. |
| 8.1.3.2 | Unrecognized Responses | Ja | Erledigt | UAC normalisiert unbekannte finale Responses auf Klassen-x00 und unbekannte provisional !=100 auf 183; dedizierte Compliance-Tests vorhanden. |
| 8.1.3.3 | Vias | Ja | Erledigt | Responses mit mehr als einem Via-Wert werden im UAC-Transaction-Matching verworfen; Testabdeckung vorhanden. |
| 8.1.3.4 | Processing 3xx Responses | Ja | Erledigt | UAC extrahiert Contact-Ziele aus 3xx Antworten, verhindert Duplikate und setzt INVITE-Transaktion rekursiv gegen Redirect-Ziele fort. |
| 8.1.3.5 | Processing 4xx Responses | Ja | Erledigt | UAC verarbeitet 401/407 (Auth), 413/415 (retry mit reduziertem Body), 416 (SIPS->SIP Downgrade) und 420 (Unsupported-Tag-Filter) mit kontrollierten Retry-Pfaden. |
| 8.2 | UAS Behavior | Nein | Teilweise | UAS-Ingress wurde fuer Pflichtheader/Request-URI-Schema gehaertet und To-Tag-Handling verbessert; vollstaendige UAS-Abarbeitung gem. 8.2.x bleibt offen. |
| 8.2.1 | Method Inspection | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.2 | Header Inspection | Nein | Teilweise | Pflichtheader-Pruefung auf Ingress-Ebene ist vorhanden; vollstaendige normative Header-Inspektion bleibt offen. |
| 8.2.2.1 | To and Request-URI | Nein | Teilweise | Unsupported Request-URI Schemes werden mit 416 abgewiesen; To-Policy-Feinheiten bleiben offen. |
| 8.2.2.2 | Merged Requests | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.2.3 | Require | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.3 | Content Processing | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.4 | Applying Extensions | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.5 | Processing the Request | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.6 | Generating the Response | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.6.1 | Sending a Provisional Response | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.2.6.2 | Headers and Tags | Nein | Teilweise | Ingress non-100 Responses erhalten To-Tag; vollstaendige Header-/Tag-Semantik ueber alle UAS-Pfade bleibt offen. |
| 8.2.7 | Stateless UAS Behavior | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 8.3 | Redirect Servers | Nein | Teilweise | UAC/UAS Basis vorhanden, volle RFC3261-Fehler- und Corner-Case-Abdeckung offen. |
| 9 | Canceling a Request | Nein | Teilweise | CANCEL-Basis vorhanden, aber nicht alle Zustands-/Race-Randfaelle vollstaendig. |
| 9.1 | Client Behavior | Nein | Teilweise | CANCEL-Basis vorhanden, aber nicht alle Zustands-/Race-Randfaelle vollstaendig. |
| 9.2 | Server Behavior | Nein | Teilweise | CANCEL-Basis vorhanden, aber nicht alle Zustands-/Race-Randfaelle vollstaendig. |
| 10 | Registrations | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.1 | Overview | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2 | Constructing the REGISTER Request | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.1 | Adding Bindings | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.1.1 | Setting the Expiration Interval of Contact Addresses | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.1.2 | Preferences among Contact Addresses | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.2 | Removing Bindings | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.3 | Fetching Bindings | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.4 | Refreshing Bindings | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.5 | Setting the Internal Clock | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.6 | Discovering a Registrar | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.7 | Transmitting a Request | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.2.8 | Error Responses | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 10.3 | Processing REGISTER Requests | Nein | Teilweise | REGISTER/Lifecycle vorhanden, volle RFC3261-Abdeckung inkl. Interop-Randfaelle offen. |
| 11 | Querying for Capabilities | Nein | Teilweise | OPTIONS vorhanden, jedoch nicht vollstaendig ueber alle Rollen/Corner-Cases. |
| 11.1 | Construction of OPTIONS Request | Nein | Teilweise | OPTIONS vorhanden, jedoch nicht vollstaendig ueber alle Rollen/Corner-Cases. |
| 11.2 | Processing of OPTIONS Request | Nein | Teilweise | OPTIONS vorhanden, jedoch nicht vollstaendig ueber alle Rollen/Corner-Cases. |
| 12 | Dialogs | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.1 | Creation of a Dialog | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.1.1 | UAS behavior | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.1.2 | UAC Behavior | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.2 | Requests within a Dialog | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.2.1 | UAC Behavior | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.2.1.1 | Generating the Request | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.2.1.2 | Processing the Responses | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.2.2 | UAS Behavior | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 12.3 | Termination of a Dialog | Nein | Teilweise | Dialog-Basis vorhanden, volle Forking-/Early-/Route-Set-Randfallabdeckung offen. |
| 13 | Initiating a Session | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.1 | Overview | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2 | UAC Processing | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.1 | Creating the Initial INVITE | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.2 | Processing INVITE Responses | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.2.1 | 1xx Responses | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.2.2 | 3xx Responses | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.2.3 | 4xx, 5xx and 6xx Responses | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.2.2.4 | 2xx Responses | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3 | UAS Processing | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3.1 | Processing of the INVITE | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3.1.1 | Progress | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3.1.2 | The INVITE is Redirected | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3.1.3 | The INVITE is Rejected | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 13.3.1.4 | The INVITE is Accepted | Nein | Teilweise | INVITE-Grundfluss vorhanden, vollstaendige 3261-Konformitaet noch offen. |
| 14 | Modifying an Existing Session | Nein | Teilweise | Session-Modifikationen (re-INVITE/UPDATE-nahe Logik) vorhanden, aber nicht vollstaendig. |
| 14.1 | UAC Behavior | Nein | Teilweise | Session-Modifikationen (re-INVITE/UPDATE-nahe Logik) vorhanden, aber nicht vollstaendig. |
| 14.2 | UAS Behavior | Nein | Teilweise | Session-Modifikationen (re-INVITE/UPDATE-nahe Logik) vorhanden, aber nicht vollstaendig. |
| 15 | Terminating a Session | Nein | Teilweise | BYE/CANCEL Termination vorhanden, aber nicht alle normativen Detailpfade abgeschlossen. |
| 15.1 | Terminating a Session with a BYE Request | Nein | Teilweise | BYE/CANCEL Termination vorhanden, aber nicht alle normativen Detailpfade abgeschlossen. |
| 15.1.1 | UAC Behavior | Nein | Teilweise | BYE/CANCEL Termination vorhanden, aber nicht alle normativen Detailpfade abgeschlossen. |
| 15.1.2 | UAS Behavior | Nein | Teilweise | BYE/CANCEL Termination vorhanden, aber nicht alle normativen Detailpfade abgeschlossen. |
| 16 | Proxy Behavior | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.1 | Overview | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.2 | Stateful Proxy | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.3 | Request Validation | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.4 | Route Information Preprocessing | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.5 | Determining Request Targets | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.6 | Request Forwarding | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.7 | Response Processing | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.8 | Processing Timer C | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.9 | Handling Transport Errors | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.10 | CANCEL Processing | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.11 | Stateless Proxy | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.12 | Summary of Proxy Route Processing | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.12.1 | Examples | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.12.1.1 | Basic SIP Trapezoid | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.12.1.2 | Traversing a Strict-Routing Proxy | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 16.12.1.3 | Rewriting Record-Route Header Field Values | Nein | Offen | Proxy-Kernrollen (stateful/stateless) nicht vollstaendig als RFC3261-Vollumfang umgesetzt. |
| 17 | Transactions | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1 | Client Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.1 | INVITE Client Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.1.1 | Overview of INVITE Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.1.2 | Formal Description | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.1.3 | Construction of the ACK Request | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.2 | Non-INVITE Client Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.2.1 | Overview of the non-INVITE Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.2.2 | Formal Description | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.3 | Matching Responses to Client Transactions | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.1.4 | Handling Transport Errors | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.2 | Server Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.2.1 | INVITE Server Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.2.2 | Non-INVITE Server Transaction | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.2.3 | Matching Requests to Server Transactions | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 17.2.4 | Handling Transport Errors | Nein | Teilweise | Transaction-Engine deutlich gehaertet, aber Vollabdeckung aller RFC3261-Corner-Cases fehlt. |
| 18 | Transport | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.1 | Clients | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.1.1 | Sending Requests | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.1.2 | Receiving Responses | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.2 | Servers | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.2.1 | Receiving Requests | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.2.2 | Sending Responses | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.3 | Framing | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 18.4 | Error Handling | Nein | Teilweise | Transportmodul (UDP/TCP/TLS/WS/WSS) vorhanden, volle RFC3261-Transportfeinheiten offen. |
| 19 | Common Message Components | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1 | SIP and SIPS Uniform Resource Indicators | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1.1 | SIP and SIPS URI Components | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1.2 | Character Escaping Requirements | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1.3 | Example SIP and SIPS URIs | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1.4 | URI Comparison | Nein | Erledigt | `SipUriProtocol.SipUriEqual` gegen die zehn Beispielpaare aus §19.1.4 getestet (`SipUriComparisonTests`) und von `ServedUserSipIdentityPolicy` angewandt. Vorher: vorhanden, aber ohne Aufrufer, ohne Test und in fuenf der zehn Beispiele falsch. |
| 19.1.5 | Forming Requests from a URI | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.1.6 | Relating SIP URIs and tel URLs | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.2 | Option Tags | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 19.3 | Tags | Nein | Teilweise | SIP-URI Basis vorhanden, aber nicht vollstaendige 3261-URI-Semantik in allen Pfaden. |
| 20 | Header Fields | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.1 | Accept | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.2 | Accept-Encoding | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.3 | Accept-Language | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.4 | Alert-Info | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.5 | Allow | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.6 | Authentication-Info | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.7 | Authorization | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.8 | Call-ID | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.9 | Call-Info | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.10 | Contact | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.11 | Content-Disposition | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.12 | Content-Encoding | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.13 | Content-Language | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.14 | Content-Length | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.15 | Content-Type | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.16 | CSeq | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.17 | Date | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.18 | Error-Info | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.19 | Expires | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.20 | From | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.21 | In-Reply-To | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.22 | Max-Forwards | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.23 | Min-Expires | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.24 | MIME-Version | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.25 | Organization | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.26 | Priority | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.27 | Proxy-Authenticate | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.28 | Proxy-Authorization | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.29 | Proxy-Require | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.30 | Record-Route | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.31 | Reply-To | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.32 | Require | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.33 | Retry-After | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.34 | Route | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.35 | Server | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.36 | Subject | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.37 | Supported | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.38 | Timestamp | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.39 | To | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.40 | Unsupported | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.41 | User-Agent | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.42 | Via | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.43 | Warning | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 20.44 | WWW-Authenticate | Nein | Teilweise | Viele Header verarbeitet, vollstaendige Header-Semantik/Mehrfachheader-Randfaelle offen. |
| 21 | Response Codes | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1 | Provisional 1xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1.1 | 100 Trying | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1.2 | 180 Ringing | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1.3 | 181 Call Is Being Forwarded | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1.4 | 182 Queued | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.1.5 | 183 Session Progress | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.2 | Successful 2xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.2.1 | 200 OK | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3 | Redirection 3xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3.1 | 300 Multiple Choices | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3.2 | 301 Moved Permanently | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3.3 | 302 Moved Temporarily | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3.4 | 305 Use Proxy | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.3.5 | 380 Alternative Service | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4 | Request Failure 4xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.1 | 400 Bad Request | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.2 | 401 Unauthorized | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.3 | 402 Payment Required | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.4 | 403 Forbidden | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.5 | 404 Not Found | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.6 | 405 Method Not Allowed | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.7 | 406 Not Acceptable | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.8 | 407 Proxy Authentication Required | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.9 | 408 Request Timeout | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.10 | 410 Gone | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.11 | 413 Request Entity Too Large | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.12 | 414 Request-URI Too Long | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.13 | 415 Unsupported Media Type | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.14 | 416 Unsupported URI Scheme | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.15 | 420 Bad Extension | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.16 | 421 Extension Required | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.17 | 423 Interval Too Brief | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.18 | 480 Temporarily Unavailable | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.19 | 481 Call/Transaction Does Not Exist | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.20 | 482 Loop Detected | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.21 | 483 Too Many Hops | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.22 | 484 Address Incomplete | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.23 | 485 Ambiguous | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.24 | 486 Busy Here | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.25 | 487 Request Terminated | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.26 | 488 Not Acceptable Here | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.27 | 491 Request Pending | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.4.28 | 493 Undecipherable | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5 | Server Failure 5xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.1 | 500 Server Internal Error | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.2 | 501 Not Implemented | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.3 | 502 Bad Gateway | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.4 | 503 Service Unavailable | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.5 | 504 Server Time-out | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.6 | 505 Version Not Supported | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.5.7 | 513 Message Too Large | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.6 | Global Failures 6xx | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.6.1 | 600 Busy Everywhere | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.6.2 | 603 Decline | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.6.3 | 604 Does Not Exist Anywhere | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 21.6.4 | 606 Not Acceptable | Nein | Teilweise | Response-Code-Verarbeitung vorhanden, aber nicht vollstaendig ueber alle Methoden/States. |
| 22 | Usage of HTTP Authentication | Nein | Teilweise | Digest-Auth vorhanden, volle 3261-Auth-Semantik ueber alle Methoden/Randfaelle offen. |
| 22.1 | Framework | Nein | Teilweise | Digest-Auth vorhanden, volle 3261-Auth-Semantik ueber alle Methoden/Randfaelle offen. |
| 22.2 | User-to-User Authentication | Nein | Teilweise | Digest-Auth vorhanden, volle 3261-Auth-Semantik ueber alle Methoden/Randfaelle offen. |
| 22.3 | Proxy-to-User Authentication | Nein | Teilweise | Digest-Auth vorhanden, volle 3261-Auth-Semantik ueber alle Methoden/Randfaelle offen. |
| 22.4 | The Digest Authentication Scheme | Nein | Teilweise | Digest-Auth vorhanden, volle 3261-Auth-Semantik ueber alle Methoden/Randfaelle offen. |
| 23 | S/MIME | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.1 | S/MIME Certificates | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.2 | S/MIME Key Exchange | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.3 | Securing MIME bodies | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4 | SIP Header Privacy and Integrity using S/MIME: Tunneling SIP | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4.1 | Integrity and Confidentiality Properties of SIP Headers | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4.1.1 | Integrity | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4.1.2 | Confidentiality | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4.2 | Tunneling Integrity and Authentication | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 23.4.3 | Tunneling Encryption | Nein | Offen | S/MIME-bezogene 3261-Anforderungen sind nicht vollstaendig implementiert. |
| 24 | Examples | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 24.1 | Registration | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 24.2 | Session Setup | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 25 | Augmented BNF for the SIP Protocol | Nein | Teilweise | ABNF-nahe Syntax wird grossenteils geparst, aber keine vollstaendige formale Vollabdeckung nachgewiesen. |
| 25.1 | Basic Rules | Nein | Teilweise | ABNF-nahe Syntax wird grossenteils geparst, aber keine vollstaendige formale Vollabdeckung nachgewiesen. |
| 26 | Security Considerations: Threat Model and Security Usage | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1 | Attacks and Threat Models | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1.1 | Registration Hijacking | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1.2 | Impersonating a Server | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1.3 | Tampering with Message Bodies | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1.4 | Tearing Down Sessions | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.1.5 | Denial of Service and Amplification | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.2 | Security Mechanisms | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.2.1 | Transport and Network Layer Security | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.2.2 | SIPS URI Scheme | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.2.3 | HTTP Authentication | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.2.4 | S/MIME | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3 | Implementing Security Mechanisms | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.1 | Requirements for Implementers of SIP | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.2 | Security Solutions | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.2.1 | Registration | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.2.2 | Interdomain Requests | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.2.3 | Peer-to-Peer Requests | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.3.2.4 | DoS Protection | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.4 | Limitations | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.4.1 | HTTP Digest | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.4.2 | S/MIME | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.4.3 | TLS | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.4.4 | SIPS URIs | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 26.5 | Privacy | Nein | Teilweise | Sicherheitsgrundlagen vorhanden, aber 3261-Security-Kapitel nicht vollstaendig umgesetzt. |
| 27 | IANA Considerations | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.1 | Option Tags | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.2 | Warn-Codes | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.3 | Header Field Names | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.4 | Method and Response Codes | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.5 | The "message/sip" MIME type. | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 27.6 | New Content-Disposition Parameter Registrations | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 28 | Changes From RFC 2543 | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 28.1 | Major Functional Changes | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 28.2 | Minor Functional Changes | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 29 | Normative References | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
| 30 | Informative References | - | N/A | Dokumentations-/Referenzabschnitt, kein direkter Implementierungsgegenstand. |
